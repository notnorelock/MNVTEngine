// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-04.

using MNVTEngine.DebugUI.ImGuiBackend;

namespace MNVTEngine.Editor.Tests;

public class ImGuiInputTests
{
    [Fact]
    public void EveryEngineKey_MapsToAnImGuiKey() => Assert.Empty(ImGuiInput.UnmappedKeys);
}
