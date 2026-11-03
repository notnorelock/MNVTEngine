using ImGuiNET;
using MNVTEngine.Assets;
using MNVTEngine.Editor.ImGuiBackend;
using MNVTEngine.Graphics;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>Inspector content for a selected asset: identity, import status, facts, references, and import settings.</summary>
internal sealed class AssetInspector(EditorContext context)
{
    private static readonly string[] Filters = Enum.GetNames<TextureFilter>();
    private static readonly string[] Wraps = Enum.GetNames<TextureWrap>();

    private readonly ScriptView _script = new();
    private Guid _editing;
    private TextureImportSettings _settings = new();
    private bool _settingsDirty;

    public void Draw(Guid guid)
    {
        var assets = context.Session.Assets;
        if (assets.Get(guid) is not { } record)
        {
            Gui.Muted("The selected asset no longer exists.");
            return;
        }

        ImGui.TextUnformatted(record.Name);
        Gui.Muted(record.Path);
        Gui.Muted($"{record.Type} - guid {record.Guid}");
        var statusColor = record.Status == AssetImportStatus.Failed ? new SysVector4(1f, 0.45f, 0.45f, 1f) : new SysVector4(0.55f, 0.85f, 0.55f, 1f);
        ImGui.TextColored(statusColor, record.Status.ToString());
        foreach (var message in record.Messages)
        {
            ImGui.TextWrapped(message);
        }

        foreach (var (key, value) in record.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ImGui.TextUnformatted($"{key}: {value}");
        }

        ImGui.Separator();
        if (record.Type == AssetTypes.Texture)
        {
            DrawTexture(guid, record);
        }
        else if (record.Type == AssetTypes.Prefab && ImGui.Button("Instantiate in scene"))
        {
            context.InstantiatePrefab(guid, null);
        }
        else if (record.Type == AssetTypes.Scene && ImGui.Button("Open scene"))
        {
            context.OpenSceneRequest = record.Path;
        }
        else if (record.Type == AssetTypes.Script)
        {
            if (ImGui.Button("Edit in Script Editor"))
            {
                context.OpenScriptRequest = assets.FullPath(guid);
            }

            _script.Draw(assets.FullPath(guid), guid, context.ScriptDebugger);
        }

        DrawReferences("References", record.Dependencies.Select(assets.Get));
        DrawReferences("Referenced by", assets.GetDependents(guid));
    }

    private void DrawTexture(Guid guid, AssetRecord record)
    {
        if (_editing != guid)
        {
            _editing = guid;
            _settings = context.Session.Assets.GetTextureSettings(guid);
            _settingsDirty = false;
        }

        if (record.Status == AssetImportStatus.Imported)
        {
            var texture = context.Resources.GetTexture(new AssetRef(guid));
            var width = Math.Min(ImGui.GetContentRegionAvail().X, 256f);
            Gui.Image(context.ImGui.Register(texture), texture, new SysVector2(width, width * texture.Height / texture.Width));
        }

        ImGui.SeparatorText("Import settings");
        var filter = (int)_settings.Filter;
        if (ImGui.Combo("Filter", ref filter, Filters, Filters.Length))
        {
            _settings.Filter = (TextureFilter)filter;
            _settingsDirty = true;
        }

        var wrap = (int)_settings.Wrap;
        if (ImGui.Combo("Wrap", ref wrap, Wraps, Wraps.Length))
        {
            _settings.Wrap = (TextureWrap)wrap;
            _settingsDirty = true;
        }

        var mipmaps = _settings.GenerateMipmaps;
        if (ImGui.Checkbox("Generate mipmaps", ref mipmaps))
        {
            _settings.GenerateMipmaps = mipmaps;
            _settingsDirty = true;
        }

        var ppu = _settings.PixelsPerUnit;
        if (ImGui.DragFloat("Pixels per unit", ref ppu, 1f, 1f, 4096f))
        {
            _settings.PixelsPerUnit = ppu;
            _settingsDirty = true;
        }

        ImGui.BeginDisabled(!_settingsDirty);
        if (ImGui.Button("Apply"))
        {
            var problems = context.Session.Assets.UpdateSettings(guid, JObject.FromObject(_settings, MnvtJson.CreateSerializer()));
            foreach (var problem in problems)
            {
                EditorLog.Warning(context.Logger, $"{record.Path}: {problem}");
            }

            _editing = Guid.Empty; // reload what was actually stored
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert"))
        {
            _editing = Guid.Empty;
        }

        ImGui.EndDisabled();
    }

    private void DrawReferences(string title, IEnumerable<AssetRecord?> records)
    {
        var list = records.ToList();
        if (list.Count == 0 || !ImGui.CollapsingHeader($"{title} ({list.Count})"))
        {
            return;
        }

        foreach (var record in list)
        {
            if (record is null)
            {
                ImGui.TextColored(new SysVector4(1f, 0.45f, 0.45f, 1f), "(missing asset)");
            }
            else if (ImGui.Selectable(record.Path))
            {
                context.Session.Selection.SelectAsset(record.Guid);
            }
        }
    }
}
