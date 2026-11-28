// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;
using MNVTEngine.UI.Widgets;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;

namespace MNVTEngine.UI.Tests;

/// <summary>UI drawn through the Vulkan backend (headless) with validation: panels, rounded corners, text, clipping, batching.</summary>
[Collection(nameof(UIGpuGroup))]
public sealed class UIRenderingTests
{
    private const int W = 320, H = 200;

    private static void Run(Action<VulkanGraphicsDevice, UIRenderer> body)
    {
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(W, H, NullLogger.Instance, new VulkanDeviceOptions { Validation = true }.WithShaderc());
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        try
        {
            using var renderer2D = new Renderer2D(device);
            using var ui = new UIRenderer(device, renderer2D.DefaultMaterial);
            body(device, ui);
        }
        finally
        {
            device.Dispose();
        }

        Assert.True(device.Validation.Errors == 0, $"{device.Validation.Errors} validation error(s): {device.Validation.LastError}");
    }

    private static byte[] Frame(VulkanGraphicsDevice device, UIRenderer renderer, UIHarness ui)
    {
        ui.Frame();
        device.SetRenderTarget(null);
        device.Clear(Color.Black);
        renderer.Render(ui.Context, W, H);
        var pixels = new byte[W * H * 4];
        device.ReadPixels(pixels);
        device.Present();
        return pixels;
    }

    private static Color Pixel(byte[] rgba, int x, int y)
    {
        var i = ((y * W) + x) * 4;
        return Color.FromBytes(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    [Fact]
    public void PanelButtonAndText_Render_InOneDrawCall_AndCachedGeometryIsReused()
    {
        Run((device, renderer) =>
        {
            using var ui = new UIHarness(W, H);
            var panel = ui.Context.Root.AddChild(new Panel { Left = 20, Top = 20, Width = 200, Height = 120, BackgroundColor = Color.Blue, CornerRadius = 16, Layout = LayoutMode.Column, Padding = 16 });
            panel.AddChild(new Label { Text = "Zażółć gęślą jaźń", FontSize = 20, TextColor = Color.White });
            panel.AddChild(new ProgressBar { Value = 0.5f, Height = 10, Width = UIUnit.Percent(100), Margin = new Thickness(0, 8, 0, 0) });

            var pixels = Frame(device, renderer, ui);
            Assert.True(Pixel(pixels, 120, 120).B > 0.9f, "panel interior is blue");
            Assert.True(Pixel(pixels, 21, 21).B < 0.5f, "rounded corner is cut");
            Assert.True(Pixel(pixels, 5, 5).B < 0.01f, "outside the panel is black");
            var lit = 0;
            for (var y = 36; y < 62; y++)
            {
                for (var x = 36; x < 210; x++)
                {
                    lit += Pixel(pixels, x, y).R > 0.6f ? 1 : 0;
                }
            }

            Assert.True(lit > 150, $"text pixels expected, found {lit}");
            Assert.Equal(1, ui.Context.Statistics.DrawCalls); // shapes and glyphs share the atlas page
            var rebuilds = ui.Context.Statistics.GeometryRebuilds;
            Frame(device, renderer, ui);
            Assert.Equal(rebuilds, ui.Context.Statistics.GeometryRebuilds); // nothing changed: geometry reused
        });
    }

    [Fact]
    public void ScrollView_ClipsItsContent()
    {
        Run((device, renderer) =>
        {
            using var ui = new UIHarness(W, H);
            var scroll = ui.Context.Root.AddChild(new ScrollView { Left = 0, Top = 0, Width = 100, Height = 50 });
            scroll.Content.AddChild(new Panel { Height = 200, BackgroundColor = Color.Red });
            var pixels = Frame(device, renderer, ui);
            Assert.True(Pixel(pixels, 40, 25).R > 0.9f);
            Assert.True(Pixel(pixels, 40, 120).R < 0.01f, "content below the view is clipped");
        });
    }

    [Fact]
    public void Gradients_BlendBetweenStops_InsideRoundedCorners_AndThroughClips()
    {
        Run((device, renderer) =>
        {
            using var ui = new UIHarness(W, H);
            var stops = new Gradient([new(0f, Color.Red), new(0.5f, Color.Green), new(1f, Color.Blue)]);
            ui.Context.Root.AddChild(new Panel { Left = 20, Top = 20, Width = 200, Height = 100, CornerRadius = 16, BackgroundGradient = stops });
            var scroll = ui.Context.Root.AddChild(new ScrollView { Left = 240, Top = 20, Width = 60, Height = 50 });
            scroll.Content.AddChild(new Panel { Height = 100, BackgroundGradient = Gradient.Linear(Color.Red, Color.Blue, 90f) });
            ui.Context.Root.AddChild(new BandBeside { Left = 120, Top = 140, Width = 40, Height = 40 });

            var pixels = Frame(device, renderer, ui);
            Assert.True(Pixel(pixels, 23, 70).R > 0.9f, "the start is the first stop");
            Assert.True(Pixel(pixels, 120, 70).G > 0.9f, "the middle stop is exact (the quads split there)");
            Assert.True(Pixel(pixels, 217, 70).B > 0.9f, "the end is the last stop");
            Assert.InRange(Pixel(pixels, 70, 70).R, 0.4f, 0.6f); // halfway between red and green
            Assert.True(Pixel(pixels, 21, 21).R < 0.5f, "rounded corner is cut");
            Assert.InRange(Pixel(pixels, 270, 68).B, 0.35f, 0.6f); // just above the clip: half way down the 100-tall gradient
            Assert.True(Pixel(pixels, 270, 90).B < 0.01f, "clipped below the view");
            Assert.True(Pixel(pixels, 100, 160).R < 0.01f && Pixel(pixels, 100, 160).G < 0.01f, "a stop beside the box paints nothing beside it");
            Assert.True(Pixel(pixels, 121, 141).R < 0.5f, "the circle's corner stays round");
        });
    }
}

/// <summary>A circle filled through the span overload with a stop far left of it, as a skeleton's shimmer band beside it.</summary>
internal sealed class BandBeside : UIElement
{
    private readonly GradientStop[] _stops = [new(-3f, Color.Green), new(0f, Color.Red), new(1f, Color.Red)];

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style) => painter.FillGradient(Bounds, _stops, 0f, 20f);
}

[CollectionDefinition(nameof(UIGpuGroup), DisableParallelization = true)]
public sealed class UIGpuGroup;
