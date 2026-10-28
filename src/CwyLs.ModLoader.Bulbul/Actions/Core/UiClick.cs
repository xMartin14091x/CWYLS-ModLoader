using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.Actions
{
    /// <summary>
    /// <c>UiClick.Invoke(path)</c>: the fallback path of an action - click the game's own
    /// UI control the way the player would, when no semantic call is available. For action families
    /// only; never exposed as a free-form action (a raw click could delete a todo past the
    /// Write-tier gate).
    /// </summary>
    /// <remarks>
    /// <para><b>Path.</b> The full hierarchy path from the scene root, as the trace's <c>BUTTON</c>
    /// lines print it (<c>Paremt/PCPlatform/Canvas/UI/…/CloudyButton</c>); a path the trace shortened
    /// (<c>…/</c>) is refused. Only active objects are found (<c>GameObject.Find</c>), like a player
    /// can only click what is on screen.</para>
    /// <para><b>Click.</b> One left click (<c>ExecuteEvents.pointerClickHandler</c>) on the object or its
    /// nearest parent that takes clicks, so a uGUI <c>Button</c> fires its <c>onClick</c> and the game's
    /// own subscribers run. A disabled or blocked control (<c>Selectable.IsInteractable</c> false) is
    /// not clicked.</para>
    /// <para><b>Deny list.</b> Refused before anything is resolved or clicked, with one WARN naming the
    /// rule: any path segment holding a word of <see cref="UiClickPolicy"/> (shop, account linking,
    /// cloud sync, SNS/browser), and any control or ancestor carrying a type in
    /// <see cref="DeniedTypes"/>.</para>
    /// <para>Main thread only (action handlers run there).</para>
    /// </remarks>
    public static class UiClick
    {
        /// <summary>
        /// Game component types whose controls (or whose children) are never clicked, with their
        /// category. Full names; matched exactly, so a type renamed by a game update only falls back
        /// to the word rules. Checked against game 1.17.3.
        /// </summary>
        public static IReadOnlyDictionary<string, string> DeniedTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Account linking and transfer.
            ["Bulbul.AuthButton"] = UiClickPolicy.Account,
            ["Bulbul.AuthUI"] = UiClickPolicy.Account,
            ["Bulbul.AccountTransferDialog"] = UiClickPolicy.Account,
            ["Bulbul.Mobile.SettingAccountView"] = UiClickPolicy.Account,

            // Shop and purchases.
            ["Bulbul.ShopOpenButtonView"] = UiClickPolicy.Shop,
            ["Bulbul.PurchasePopover"] = UiClickPolicy.Shop,
            ["Bulbul.Mobile.PurchaseButtonWithPoint"] = UiClickPolicy.Shop,
            ["Bulbul.Mobile.ShopWindowView"] = UiClickPolicy.Shop,
            ["Bulbul.Mobile.SettingShopView"] = UiClickPolicy.Shop,
            ["Bulbul.Mobile.ShopBannerUI"] = UiClickPolicy.Shop,
            ["Bulbul.Mobile.FacilityShopWindow"] = UiClickPolicy.Shop,

            // The browser: hyperlinks (Bulbul.TMP_Hyperlink opens Application.OpenURL) and the terms dialog (Bulbul.TermsDialog).
            ["Bulbul.TMP_Hyperlink"] = UiClickPolicy.SnsBrowser,
            ["Bulbul.TermsDialog"] = UiClickPolicy.SnsBrowser,
        };

        /// <summary>Clicks the control at <paramref name="path"/> unless the deny list or the control's state refuses it. Main thread only; never throws.</summary>
        /// <param name="path">The full hierarchy path from the scene root.</param>
        public static UiClickResult Invoke(string path) => Run(path, click: true);

        /// <summary>Resolves and checks the control at <paramref name="path"/> like <see cref="Invoke"/>, without clicking. Main thread only; never throws.</summary>
        /// <param name="path">The full hierarchy path from the scene root.</param>
        public static UiClickResult Probe(string path) => Run(path, click: false);

        /// <summary>
        /// Semantic call first, UI click as fallback: returns <paramref name="semantic"/> when it
        /// reached the game or was refused on purpose (Ok, InvalidArgs, Denied, Declined, or any result
        /// with a path); otherwise clicks <paramref name="path"/> and returns that outcome
        /// (<see cref="ActionPath.UiClick"/> when clicked).
        /// </summary>
        /// <param name="semantic">The outcome of the semantic attempt.</param>
        /// <param name="path">The control that does the same thing.</param>
        /// <param name="what">What the click does, for the Ok message (e.g. <c>pressed play</c>).</param>
        public static ActionResult Fallback(ActionResult semantic, string path, string what)
        {
            if (semantic != null && (semantic.Path != ActionPath.None
                                     || semantic.Status == ActionStatus.Ok
                                     || semantic.Status == ActionStatus.InvalidArgs
                                     || semantic.Status == ActionStatus.Denied
                                     || semantic.Status == ActionStatus.Declined))
            {
                return semantic;
            }

            var click = Invoke(path);
            if (click.Status == UiClickStatus.Clicked || click.Status == UiClickStatus.Denied)
            {
                return click.ToActionResult(what);
            }

            return ActionResult.Failed(
                (semantic is null ? "no semantic path" : "semantic: " + semantic.Message) +
                "; UI fallback: " + click.Status + ": " + click.Detail);
        }

        /// <summary>The full root-first hierarchy path of a transform (never shortened).</summary>
        /// <param name="leaf">The transform.</param>
        public static string PathOf(Transform? leaf)
        {
            var names = new List<string>();
            var current = leaf;
            for (var depth = 0; current != null && depth < 128; depth++)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static UiClickResult Run(string? path, bool click)
        {
            var requested = (path ?? string.Empty).Trim().TrimStart('/');
            try
            {
                if (!MainThread.IsMainThread)
                {
                    return new UiClickResult(UiClickStatus.Failed, requested, null, "UI clicks run on the main thread only");
                }

                if (requested.Length == 0)
                {
                    return new UiClickResult(UiClickStatus.NotFound, requested, null, "no path given");
                }

                var verdict = UiClickPolicy.CheckPath(requested);
                if (verdict.IsDenied)
                {
                    return Refuse(requested, string.Empty, verdict, click);
                }

                if (requested.StartsWith(UiPath.Elision, StringComparison.Ordinal))
                {
                    return new UiClickResult(UiClickStatus.NotFound, requested, null,
                        "the path is shortened; give the full path from the scene root");
                }

                var target = GameObject.Find("/" + requested);
                if (target == null)
                {
                    return new UiClickResult(UiClickStatus.NotFound, requested, null, "no active object has this path");
                }

                var actual = PathOf(target.transform);
                verdict = UiClickPolicy.CheckPath(actual);
                if (verdict.IsDenied)
                {
                    return Refuse(requested, actual, verdict, click);
                }

                verdict = UiClickPolicy.CheckTypes(ComponentTypes(target.transform), DeniedTypes);
                if (verdict.IsDenied)
                {
                    return Refuse(requested, actual, verdict, click);
                }

                var receiver = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
                if (receiver == null)
                {
                    return new UiClickResult(UiClickStatus.NoHandler, requested, actual, "neither the object nor its parents take a click");
                }

                var receiverPath = PathOf(receiver.transform);
                var selectable = receiver.GetComponent<Selectable>();
                if (selectable != null && !selectable.IsInteractable())
                {
                    return new UiClickResult(UiClickStatus.NotInteractable, requested, receiverPath, "the control is disabled or blocked");
                }

                if (!click)
                {
                    return new UiClickResult(UiClickStatus.Probed, requested, receiverPath, null);
                }

                var data = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left,
                    clickCount = 1,
                    eligibleForClick = true,
                    pointerPress = receiver,
                    rawPointerPress = target,
                };

                var handled = ExecuteEvents.Execute(receiver, data, ExecuteEvents.pointerClickHandler);
                ConsoleEvents.File(ActionAudit.TraceSys, "uiclick", ("path", receiverPath), ("requested", requested), ("handled", handled));
                return handled
                    ? new UiClickResult(UiClickStatus.Clicked, requested, receiverPath, null)
                    : new UiClickResult(UiClickStatus.Failed, requested, receiverPath, "the click was not handled");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("ACTION UI click on " + requested + " failed: " + ex.GetType().Name + ": " + ex.Message);
                return new UiClickResult(UiClickStatus.Failed, requested, null, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static UiClickResult Refuse(string requested, string actual, UiClickVerdict verdict, bool click)
        {
            var fields = new (string Key, object? Value)[]
            {
                ("path", actual.Length == 0 ? requested : actual),
                ("rule", verdict.Reason),
                ("category", verdict.Category),
                ("probe", !click),
            };

            if (click)
            {
                ConsoleEvents.EmitAt(ActionAudit.TraceSys, "uiclick", ConsoleTags.Action, ConsoleLevel.Warn,
                    "UI click refused: " + verdict.Category, "path,rule", fields);
            }
            else
            {
                ConsoleEvents.File(ActionAudit.TraceSys, "uiclick", fields);
            }

            return new UiClickResult(UiClickStatus.Denied, requested, actual, verdict.Reason);
        }

        private static List<string?> ComponentTypes(Transform leaf)
        {
            var names = new List<string?>();
            var current = leaf;
            for (var depth = 0; current != null && depth < 128; depth++)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    // A missing script shows up as a null component.
                    if (component != null)
                    {
                        names.Add(component.GetType().FullName);
                    }
                }

                current = current.parent;
            }

            return names;
        }
    }
}
