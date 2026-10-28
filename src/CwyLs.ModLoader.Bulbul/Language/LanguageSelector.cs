using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// Adds one entry per pack language to the game's own language selector, cloned from the
    /// English entry so it looks and behaves exactly like the seven the game ships.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selector is not generated: it is seven hand-placed buttons under one <c>Content</c> object
    /// with a <c>VerticalLayoutGroup</c>, wired at runtime in <c>SettingUI.Setup</c>. So a clone of
    /// <c>_languageENInteractableUI</c>'s object under the same parent lays itself out as the eighth
    /// (ninth, ...) row; its click is re-wired to <c>SettingService.ChangeGameLanguage(value)</c>, the
    /// same call the game's own buttons make.
    /// </para>
    /// <para>
    /// The game's highlight/caption code is a local function inside <c>SettingUI</c> whose
    /// compiler-generated name is not stable, so it is not patched. Instead this class
    /// subscribes to <c>SettingData.GameLanguage</c> <em>after</em> the game does - R3 notifies in
    /// subscription order - and sets our entries' highlight and the pulldown caption itself. For a
    /// pack value the game's switch turns its seven highlights off and leaves the caption alone,
    /// which is exactly the state ours then completes.
    /// </para>
    /// <para>
    /// The game sized the pulldown for its seven rows and the settings page clips anything longer,
    /// so the rows are not made room for by growing the pulldown: <see cref="LanguageSelectorList"/>
    /// makes the list scroll inside the game's own open height.
    /// </para>
    /// </remarks>
    internal static class LanguageSelector
    {
        /// <summary>Name prefix of every entry we add, so a scene inspector shows whose they are.</summary>
        internal const string EntryPrefix = "LanguageSelectButton_cwy_";

        private static readonly List<Entry> Entries = new List<Entry>();

        private static IDisposable? _viewSubscription;
        private static PulldownListUI? _pulldown;

        /// <summary>What the last setup did, for the smoke report. Never null.</summary>
        internal static string Detail { get; private set; } = "the settings window has not been set up yet";

        /// <summary>The entries currently in the selector, for the smoke check.</summary>
        internal static IReadOnlyList<Entry> Current => Entries;

        /// <summary>Called from the <c>SettingUI.Setup</c> postfix, once per settings window.</summary>
        /// <param name="ui">The settings window that just set itself up.</param>
        internal static void OnSetup(Game.SettingUI ui)
        {
            if (ui == null || !LanguageFeature.CanApply || LanguageFeature.Registry.Count == 0)
            {
                return;
            }

            var template = Read<Game.InteractableUI>(ui, "_languageENInteractableUI");
            var pulldown = Read<PulldownListUI>(ui, "_languagePulldownList");
            var service = Read<Game.SettingService>(ui, "_settingService");
            var sound = Read<Game.SystemSeService>(ui, "_systemSeService");

            if (template == null || pulldown == null || service == null)
            {
                Detail = "SettingUI is missing " + (template == null ? "_languageENInteractableUI " : string.Empty)
                    + (pulldown == null ? "_languagePulldownList " : string.Empty)
                    + (service == null ? "_settingService" : string.Empty);
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " " + Detail);
                return;
            }

            _viewSubscription?.Dispose();
            _viewSubscription = null;
            Entries.Clear();
            _pulldown = pulldown;

            var parent = template.transform.parent;
            var created = 0;

            foreach (var language in LanguageFeature.Registry.Languages)
            {
                var name = EntryPrefix + language.Code;
                var existing = parent.Find(name);
                GameObject clone;
                if (existing != null)
                {
                    clone = existing.gameObject;
                }
                else
                {
                    clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
                    clone.name = name;
                    clone.transform.SetAsLastSibling();
                    created++;
                }

                var label = clone.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null)
                {
                    label.text = language.Caption;
                }

                var button = clone.GetComponent<Button>();
                var wired = false;
                if (button != null)
                {
                    // Non-persistent listeners are not copied by Instantiate, and the template's
                    // persistent list is empty, so this only guards a re-used entry.
                    button.onClick.RemoveAllListeners();
                    var value = language.Value;
                    button.onClick.AddListener(() => Choose(service, sound, value));
                    wired = true;
                }

                // The clone's Awake has not run if the settings window is inactive (it usually is:
                // FacilitySetting.Setup deactivates it straight after), and
                // Activate/DeactivateUseUI need the tween arrays SetupCore creates - which is why the
                // game calls Setup() on each of its own seven first (in SettingUI.Setup).
                var interactable = clone.GetComponent<Game.InteractableUI>();
                if (interactable != null)
                {
                    interactable.Setup();
                }

                Entries.Add(new Entry(language, clone, interactable, wired));
            }

            var height = ArrangeList(pulldown, template, parent as RectTransform, created);

            _viewSubscription = Game.SaveDataManager.Instance.SettingData.GameLanguage.Subscribe(ShowSelection);

            Detail = Entries.Count + " entr" + (Entries.Count == 1 ? "y" : "ies") + " under '" + parent.name + "'"
                + (created > 0 ? " (" + created + " added)" : " (re-used)") + "; " + LanguageSelectorList.Detail;

            var list = LanguageSelectorList.Mode.ToString().ToLowerInvariant();
            var plan = LanguageSelectorList.Plan;
            foreach (var entry in Entries)
            {
                ConsoleEvents.Emit(
                    LanguageFeature.TraceSys,
                    "entry",
                    ConsoleTags.Language,
                    "selector entry added",
                    "caption,index,pulldown,list",
                    ("caption", entry.Language.Caption),
                    ("index", entry.Object.transform.GetSiblingIndex()),
                    ("pulldown", float.IsNaN(height) ? (object)"unchanged" : Math.Round(height, 1)),
                    ("list", list),
                    ("rows", plan == null ? "-" : plan.VisibleRows + "/" + plan.TotalRows),
                    ("viewport", plan == null ? (object)"-" : Math.Round(plan.ViewportHeight, 1)),
                    ("maxOffset", plan == null ? (object)"-" : Math.Round(plan.MaxOffset, 1)),
                    ("listTop", plan == null ? (object)"-" : Math.Round(plan.ListTop, 1)),
                    ("scrollbar", LanguageSelectorList.ScrollbarSource),
                    ("code", entry.Language.Code),
                    ("value", entry.Language.Value));
            }
        }

        /// <summary>
        /// Makes every row reachable. Normally the list scrolls inside the game's open height
        /// (<see cref="LanguageSelectorList"/>); when the pulldown is not built the way that expects
        /// (a game update), the pulldown grows instead - the older behaviour - with one warning.
        /// </summary>
        /// <returns>The pulldown's open height now, or NaN when it was left as it was.</returns>
        private static float ArrangeList(PulldownListUI pulldown, Game.InteractableUI template, RectTransform? content, int created)
        {
            string reason;
            if (content == null)
            {
                reason = "the entries' parent '" + template.transform.parent.name + "' has no RectTransform";
            }
            else
            {
                try
                {
                    if (LanguageSelectorList.TryArrange(pulldown, content, EntryPrefix, out reason))
                    {
                        return LanguageSelectorList.GameOpenHeight;
                    }
                }
                catch (Exception ex)
                {
                    // TryArrange changes nothing before its own guarded build step, so the list is
                    // still the game's here; the reason goes into the one warning below.
                    reason = "arranging the list failed: " + ex.GetType().Name + ": " + ex.Message;
                }
            }

            CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " the language list cannot scroll (" + reason
                + "); making the pulldown taller instead, so the settings page may cut off its last rows");
            LanguageSelectorList.MarkGrown(reason);
            return created > 0 ? GrowPulldown(pulldown, template, created) : float.NaN;
        }

        /// <summary>The same two calls the game's own buttons make (wired in <c>SettingUI.Setup</c>).</summary>
        private static void Choose(Game.SettingService service, Game.SystemSeService? sound, int value)
        {
            try
            {
                service.ChangeGameLanguage((Game.GameLanguageType)value);
                sound?.PlayClick();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " switching to " + LanguageFeature.Registry.ToCode(value)
                    + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Highlights our entry for the current language and names it in the pulldown caption.</summary>
        private static void ShowSelection(Game.GameLanguageType current)
        {
            try
            {
                var value = (int)current;
                foreach (var entry in Entries)
                {
                    if (entry.Interactable == null)
                    {
                        continue;
                    }

                    if (entry.Language.Value == value)
                    {
                        entry.Interactable.ActivateUseUI();
                        if (_pulldown != null)
                        {
                            _pulldown.ChangeSelectContentText(entry.Language.Caption);
                        }
                    }
                    else
                    {
                        entry.Interactable.DeactivateUseUI();
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not update the selector highlight: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Fallback only (see <see cref="ArrangeList"/>): makes the open pulldown tall enough for the
        /// new rows, the template row's height plus the layout spacing per row
        /// (<c>PulldownListUI.SetTargetPullDownSizeDelta</c>). The settings page's
        /// own mask may then cut off the last rows, which is the defect the scrolling list removed.
        /// </summary>
        private static float GrowPulldown(PulldownListUI pulldown, Game.InteractableUI template, int rows)
        {
            try
            {
                var field = AccessTools.Field(typeof(PulldownListUI), "_openPullDownSizeDeltaY");
                if (field is null || !(field.GetValue(pulldown) is float current))
                {
                    CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " PulldownListUI._openPullDownSizeDeltaY is not readable; the list may clip");
                    return float.NaN;
                }

                var rowHeight = template.RectTransform.rect.height;
                var layout = template.transform.parent.GetComponent<VerticalLayoutGroup>();
                var spacing = layout != null ? layout.spacing : 0f;
                var target = SelectorListLayout.GrownOpenHeight(current, rows, rowHeight, spacing);
                pulldown.SetTargetPullDownSizeDelta(target);
                return target;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not grow the pulldown: " + ex.GetType().Name + ": " + ex.Message);
                return float.NaN;
            }
        }

        private static T? Read<T>(Game.SettingUI ui, string field)
            where T : class
        {
            try
            {
                return AccessTools.Field(typeof(Game.SettingUI), field)?.GetValue(ui) as T;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " SettingUI." + field + " is not readable: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>One selector entry we added.</summary>
        internal sealed class Entry
        {
            internal Entry(PackLanguage language, GameObject obj, Game.InteractableUI? interactable, bool wired)
            {
                Language = language;
                Object = obj;
                Interactable = interactable;
                Wired = wired;
            }

            /// <summary>The language it selects.</summary>
            internal PackLanguage Language { get; }

            /// <summary>Its GameObject.</summary>
            internal GameObject Object { get; }

            /// <summary>Its highlight component.</summary>
            internal Game.InteractableUI? Interactable { get; }

            /// <summary>True when the entry has a Button and our click handler was added to it.</summary>
            internal bool Wired { get; }

            /// <summary>The caption its label shows now.</summary>
            internal string Caption
            {
                get
                {
                    var label = Object == null ? null : Object.GetComponentInChildren<TextMeshProUGUI>(true);
                    return label == null ? string.Empty : label.text ?? string.Empty;
                }
            }

            /// <summary><c>th@7</c>.</summary>
            public override string ToString() =>
                Language.Code + "@" + (Object == null ? "destroyed" : Object.transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
        }
    }
}
