using System.Numerics;

namespace Dalamud.Configuration
{
    public interface IPluginConfiguration { int Version { get; set; } }
}

namespace wahventory.Services
{
    public sealed class FilterHiddenCounts
    {
        public int InGearset, Indisposable, HQ, Collectables, UniqueUntradeable,
            HighLevelGear, MarketPriceAbove, MarketPriceBelow;
    }
}

namespace wahventory.UI
{
    public static class Theme
    {
        public static readonly Vector4 ColorBlue = new(0, 0, 1, 1);
        public static readonly Vector4 ColorSubdued = new(0.5f, 0.5f, 0.5f, 1);
    }
}

namespace Dalamud.Bindings.ImGui
{
    public enum ImGuiCol { Text }
    [Flags] public enum ImGuiHoveredFlags { None = 0, AllowWhenDisabled = 1 }

    public sealed record WidgetVisit(string Kind, string Label, bool Disabled, bool InteractionAttempted,
        object Before, object After, bool Changed);

    // Interaction fake, not a filter implementation: disabled scopes suppress
    // attempted user edits, exactly where the production component places them.
    public static class ImGui
    {
        public static readonly Dictionary<string, bool> CheckboxEdits = new();
        public static readonly Dictionary<string, int> InputEdits = new();
        public static readonly HashSet<string> Hovered = new();
        public static readonly List<WidgetVisit> Visits = new();
        public static readonly List<string> Texts = new();
        public static readonly List<string> Tooltips = new();
        public static int DisabledDepth;
        private static string _lastItem = "";

        public static void Reset()
        {
            CheckboxEdits.Clear(); InputEdits.Clear(); Hovered.Clear(); Visits.Clear();
            Texts.Clear(); Tooltips.Clear(); DisabledDepth = 0; _lastItem = "";
        }
        public static bool Checkbox(string label, ref bool value)
        {
            _lastItem = label;
            var before = value;
            var attempted = CheckboxEdits.TryGetValue(label, out var requested);
            var changed = DisabledDepth == 0 && attempted && requested != value;
            if (changed) value = requested;
            Visits.Add(new("Checkbox", label, DisabledDepth != 0, attempted, before, value, changed));
            return changed;
        }
        public static bool InputInt(string label, ref int value, int step, int stepFast)
        {
            _lastItem = label;
            var before = value;
            var attempted = InputEdits.TryGetValue(label, out var requested);
            var changed = DisabledDepth == 0 && attempted && requested != value;
            if (changed) value = requested;
            Visits.Add(new("InputInt", label, DisabledDepth != 0, attempted, before, value, changed));
            return changed;
        }
        public static bool IsItemHovered(ImGuiHoveredFlags flags = ImGuiHoveredFlags.None) =>
            Hovered.Contains(_lastItem) && (DisabledDepth == 0 || flags.HasFlag(ImGuiHoveredFlags.AllowWhenDisabled));
        public static void SetTooltip(string text) => Tooltips.Add(text);
        public static void Text(string text) => Texts.Add(text);
        public static void TextColored(Vector4 color, string text) => Texts.Add(text);
        public static void TextWrapped(string text) => Texts.Add(text);
        public static void SameLine(float offsetFromStartX = 0) { }
        public static void Separator() { }
        public static void Spacing() { }
        public static void AlignTextToFramePadding() { }
        public static void SetNextItemWidth(float width) { }
        public static Vector2 GetContentRegionAvail() => new(240, 500);
        public static Vector2 GetWindowContentRegionMax() => new(240, 500);
        public static Vector2 CalcTextSize(string text) => new(text.Length * 7, 14);
    }
}

namespace Dalamud.Interface.Utility.Raii
{
    using Dalamud.Bindings.ImGui;
    public static class ImRaii
    {
        public static IDisposable Disabled(bool disabled) => new DisabledScope(disabled);
        public static IDisposable PushColor(ImGuiCol col, Vector4 color) => new NoopScope();
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
        private sealed class DisabledScope : IDisposable
        {
            private readonly bool _disabled;
            private bool _disposed;
            public DisabledScope(bool disabled)
            {
                _disabled = disabled;
                if (disabled) ImGui.DisabledDepth++;
            }
            public void Dispose()
            {
                if (_disposed) throw new InvalidOperationException("Scope disposed twice");
                _disposed = true;
                if (_disabled) ImGui.DisabledDepth--;
            }
        }
    }
}
