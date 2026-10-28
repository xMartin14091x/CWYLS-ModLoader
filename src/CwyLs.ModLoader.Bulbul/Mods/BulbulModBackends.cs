using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.UI;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Mods;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Mods
{
    /// <summary>
    /// The game adapter's half of the code-mod front door: it hands CwyLs.ModLoader
    /// the game-specific helper facets - events, Satone, language, windows - as implementations of
    /// interfaces the framework declares, so the framework itself never names a game type. Each
    /// backend is a thin pass-through to the capability that already exists (the event bus, the
    /// Satone APIs, the pack-language registry and the game-style windows).
    /// </summary>
    internal static class BulbulModBackends
    {
        /// <summary>Registers every backend. Called once from <c>BulbulPlugin.Awake</c>, before any mod loads.</summary>
        internal static void Register()
        {
            ModBackends.UseEvents(new EventsBackend());
            ModBackends.UseSatone(new SatoneBackend());
            ModBackends.UseLanguage(new LanguageBackend());
            ModBackends.UseUi(new UiBackend());
        }

        /// <summary>The typed game event bus (<see cref="CWYEvents"/>).</summary>
        private sealed class EventsBackend : IModEventSource
        {
            public IDisposable Subscribe<T>(Action<T> handler)
                where T : ICwyEvent =>
                CWYEvents.Subscribe(handler);

            public IDisposable SubscribeAny(Action<AnyEvent> handler) => CWYEvents.SubscribeAny(handler);
        }

        /// <summary>The Satone API (<see cref="Satone.Motion.Satone"/>).</summary>
        private sealed class SatoneBackend : ICwyModSatone
        {
            public bool IsAvailable => true;

            public bool IsSaying => Satone.Motion.Satone.IsSaying;

            public bool CanInteract => Satone.Motion.Satone.CanInteract;

            public string State => Satone.Motion.Satone.State;

            public bool Face(string nameOrId) => Satone.Motion.Satone.Face(nameOrId);

            public bool FaceReset() => Satone.Motion.Satone.FaceReset();

            public bool Motion(string nameOrId, bool trigger = false) => Satone.Motion.Satone.Motion(nameOrId, trigger);

            public bool LookAt(float scale, float seconds) => Satone.Motion.Satone.LookAt(scale, seconds);

            public bool LookReset() => Satone.Motion.Satone.LookReset();

            public RestoreResult RestoreDefaults(RestoreScope scope = RestoreScope.All) =>
                Satone.Motion.Satone.RestoreDefaults(scope);

            public SayHandle Say(string text) => Satone.Motion.Satone.Say(SayRequest.For(text));

            public SayHandle Say(SayRequest request) => Satone.Motion.Satone.Say(request);

            public SayHandle SayWithClip(SayRequest request, AudioClip clip) => Satone.Motion.Satone.Say(request, clip);
        }

        /// <summary>The display language (<see cref="LanguageFeature"/>).</summary>
        private sealed class LanguageBackend : ICwyModLanguage
        {
            public bool IsAvailable => LanguageFeature.IsReady;

            public string CurrentCode => LanguageFeature.IsReady ? LanguageFeature.CurrentCode : string.Empty;

            public bool IsPackLanguage => LanguageFeature.IsReady && LanguageFeature.Active != null;
        }

        /// <summary>Game-style windows (<see cref="GameStyleWindow"/>).</summary>
        private sealed class UiBackend : IModUiSource
        {
            public ICwyModWindow? CreateWindow(string owner, CwyWindowOptions options, out string problem)
            {
                var gameOptions = new GameStyleWindowOptions
                {
                    Name = "CWY.Mod " + owner,
                    Title = options.Title ?? string.Empty,
                    MinWidth = options.MinWidth,
                    MinHeight = options.MinHeight,
                };

                if (!GameStyleWindow.TryCreate(gameOptions, out var window, out problem) || window is null)
                {
                    return null;
                }

                try
                {
                    return new ModWindowAdapter(owner, window, options.InputPlaceholder);
                }
                catch (Exception ex)
                {
                    problem = "the window's parts could not be built: " + ex.GetType().Name + ": " + ex.Message;
                    CwyLsModLoader.Log.LogError("MODS " + owner + ": " + problem);
                    window.Destroy();
                    return null;
                }
            }
        }

        /// <summary>
        /// A <see cref="GameStyleWindow"/> with a scrolling line view filling its body and, when
        /// asked for, a one-line input along the bottom - the smallest useful mod window until the
        /// window toolkit adds more parts.
        /// </summary>
        private sealed class ModWindowAdapter : ICwyModWindow
        {
            private const float RowHeight = 44f;
            private const float Gap = 8f;
            private const int MaxLines = 500;

            private readonly string _owner;
            private readonly GameStyleWindow _window;
            private readonly GameStyleTextView _view;
            private readonly GameStyleInputLine? _input;
            private readonly List<string> _lines = new List<string>();
            private int _appended;
            private long _version;

            internal ModWindowAdapter(string owner, GameStyleWindow window, string? inputPlaceholder)
            {
                _owner = owner;
                _window = window;

                var body = window.Body;
                _view = window.AddTextView(body);
                GameStyleUi.Stretch(_view.Rect, 0f, 0f, 0f, inputPlaceholder is null ? 0f : RowHeight + Gap);

                if (inputPlaceholder != null)
                {
                    var row = GameStyleUi.Child(body, "Row");
                    row.anchorMin = new Vector2(0f, 0f);
                    row.anchorMax = new Vector2(1f, 0f);
                    row.pivot = new Vector2(0.5f, 0f);
                    row.offsetMin = Vector2.zero;
                    row.offsetMax = new Vector2(0f, RowHeight);

                    _input = window.AddInputLine(row, inputPlaceholder);
                    GameStyleUi.Stretch(_input.Rect);
                    _input.Visible = true;
                    _input.Submitted += OnSubmitted;
                }

                window.CloseClicked += () => CloseClicked?.Invoke();
                window.Updated += Render;
                window.Failed += ex => CwyLsModLoader.Log.LogError(
                    "MODS " + _owner + ": a window hid itself after its update threw " + ex.GetType().Name + ": " + ex.Message);
            }

            public event Action? CloseClicked;

            public event Action<string>? Submitted;

            public string Title
            {
                get => _window.Title;
                set => _window.Title = value ?? string.Empty;
            }

            public string Status
            {
                get => _window.Status;
                set => _window.Status = value ?? string.Empty;
            }

            public bool IsShown => _window.IsShown;

            public bool IsAlive => _window.IsAlive;

            public void Show() => _window.Show();

            public void Hide() => _window.Hide();

            public void Destroy() => _window.Destroy();

            public void SetLines(IReadOnlyList<string>? lines)
            {
                _lines.Clear();
                if (lines != null)
                {
                    foreach (var line in lines)
                    {
                        _lines.Add(line ?? string.Empty);
                    }
                }

                Trim();
                _appended = 0;
                _version++;
            }

            public void AppendLine(string line)
            {
                _lines.Add(line ?? string.Empty);
                _appended++;
                Trim();
            }

            private void Trim()
            {
                if (_lines.Count <= MaxLines)
                {
                    return;
                }

                _lines.RemoveRange(0, _lines.Count - MaxLines);
                _version++;
            }

            private void Render()
            {
                _view.Render(_lines.Count, _appended, _version, index => _lines[index]);
                _appended = 0;
            }

            private void OnSubmitted(string text)
            {
                _input?.SetText(string.Empty);
                Submitted?.Invoke(text);
            }
        }
    }
}
