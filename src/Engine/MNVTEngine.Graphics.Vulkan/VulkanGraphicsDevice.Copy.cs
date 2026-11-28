// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Graphics.Vulkan.Resources;
using Silk.NET.Vulkan;

namespace MNVTEngine.Graphics.Vulkan;

/// <summary>Copies between targets with a scaling blit (the UI's frosted glass blurs through a chain of these).</summary>
public sealed unsafe partial class VulkanGraphicsDevice
{
    public bool CanCopyColor => _currentTarget is not null || _headlessBackBuffer is not null || _swapchain is not { SupportsReadback: false };

    public void CopyColor(RenderTarget2D? source, RenderTarget2D destination)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        BeginRecording();
        var from = source is IVulkanTexture texture ? texture.Image : CurrentImage();
        if (from is null)
        {
            return; // minimized: nothing to copy
        }

        if (from.IsSwapchainImage && !_swapchain!.SupportsReadback)
        {
            throw new NotSupportedException("This surface does not allow copying from the swapchain.");
        }

        var to = ((IVulkanTexture)destination).Image;
        EndRendering(); // layout transitions are not allowed inside a rendering instance; the next draw resumes (loads) it
        Barriers.Transition(Context.Vk, _cmd, from, ImageLayout.TransferSrcOptimal, Statistics);
        Barriers.Transition(Context.Vk, _cmd, to, ImageLayout.TransferDstOptimal, Statistics);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D(from.Width, from.Height, 1);
        blit.DstOffsets[1] = new Offset3D(to.Width, to.Height, 1);
        Context.Vk.CmdBlitImage(_cmd, from.Handle, ImageLayout.TransferSrcOptimal, to.Handle, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
        Barriers.Transition(Context.Vk, _cmd, to, ImageLayout.ShaderReadOnlyOptimal, Statistics);
    }
}
