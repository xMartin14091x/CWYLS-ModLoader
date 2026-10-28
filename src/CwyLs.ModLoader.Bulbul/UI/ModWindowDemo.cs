using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Ui;
using CwyLs.ModLoader.Mods;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The demo window behind <c>windows demo</c> and the loud smoke check <c>s.ui.demo</c>: every
    /// part kind on the public API, so the final pass can look at layout, Thai rendering, typing with
    /// the input method, streaming, Escape, Hide UI and remembered places without a code mod. Owned by
    /// the framework (<c>cwy.ui</c>); object <c>CWY.Mod.cwy.ui.demo</c>.
    /// </summary>
    internal static class ModWindowDemo
    {
        /// <summary>The owner id (a framework feature, not a mod).</summary>
        internal const string OwnerId = "cwy.ui";

        private const int CharactersPerFrame = 2;

        private static readonly Queue<KeyValuePair<ICwyChatBubble, string>> Streams = new Queue<KeyValuePair<ICwyChatBubble, string>>();
        private static ICwyWindow? _window;
        private static IDisposable? _ticker;
        private static int _streamed;

        /// <summary>True while the demo window exists and is shown.</summary>
        internal static bool IsOpen => _window != null && _window.IsAlive && _window.IsShown;

        /// <summary>Opens the demo window (builds it the first time). Main thread.</summary>
        /// <returns>What happened, for the console.</returns>
        internal static string Open()
        {
            if (_window != null && _window.IsAlive)
            {
                _window.Show();
                return "demo window shown again (" + _window.ObjectName + ")";
            }

            var spec = new CwyWindowSpec("demo")
            {
                Title = "CWY windows - demo",
                Width = 720f,
                Height = 640f,
                Parts =
                {
                    new CwyTabsSpec("tabs") { Labels = { "Chat", "List", "Log" } },
                    new CwyChatSpec("chat") { Tab = 0 },
                    new CwyInputSpec("say") { Tab = 0, Lines = 2, Placeholder = "พิมพ์ภาษาไทยได้เลย - Enter ส่ง, Shift+Enter ขึ้นบรรทัดใหม่" },
                    new CwyButtonsSpec("actions") { Tab = 0, Buttons = { new CwyButtonSpec("clear", "clear"), new CwyButtonSpec("close", "close") } },
                    new CwyListSpec("items") { Tab = 1 },
                    new CwyTextSpec("log") { Tab = 2 },
                },
            };

            var result = ModWindows.Create(ModWindowOwner.Framework(OwnerId), spec);
            var window = result.Window;
            if (window is null)
            {
                return "demo window refused: " + result.Problem;
            }

            _window = window;
            Wire(window);
            Seed(window);
            _ticker ??= MainThread.AddTicker("ui.demo", Stream);
            window.Show();
            window.Input("say").Focus();
            return "demo window open (" + window.ObjectName + "): type Thai in the box, Enter sends, Shift+Enter breaks the line, "
                + "Esc closes it; drag it and reopen with 'windows demo' to see the place remembered";
        }

        private static void Wire(ICwyWindow window)
        {
            var chat = window.Chat("chat");
            var input = window.Input("say");
            var log = window.Text("log");
            var list = window.List("items");

            input.Submitted += text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                chat.Add(CwyChatRole.Player, text);
                Reply(chat, "ได้รับแล้ว: " + text);
                log.AppendLine("submitted " + text.Length + " chars: " + CwyRichText.Escape(text));
            };

            chat.ButtonClicked += (bubble, button) =>
            {
                log.AppendLine("bubble " + bubble.Id + " button " + button);
                if (button == "replay" && bubble.Tag is string again)
                {
                    Reply(chat, again);
                }
            };

            window.Buttons("actions").Clicked += id =>
            {
                log.AppendLine("button " + id);
                if (id == "clear")
                {
                    chat.Clear();
                    chat.Add(CwyChatRole.System, "cleared");
                }
                else if (id == "close")
                {
                    window.Hide();
                }
            };

            window.Tabs("tabs").SelectedChanged += tab => log.AppendLine("tab " + tab);
            list.ItemClicked += index => log.AppendLine("list item " + index + ": " + CwyRichText.Escape(list.Items[index]));
            window.CloseRequested += reason => log.AppendLine("closed by " + reason);
        }

        private static void Seed(ICwyWindow window)
        {
            var chat = window.Chat("chat");
            chat.Add(CwyChatRole.System, "S-UI demo: chat, text box, buttons, tabs");
            Reply(chat, "สวัสดีจ้า ลองพิมพ์ภาษาไทยดูนะ วรรณยุกต์ต้องอยู่เหนือสระ: " + ThaiMarkCheck.Sample);
            Reply(chat, "Hello! This bubble streams in two characters a frame, with the typing dots until it ends.");

            window.List("items").SetItems(new[]
            {
                ThaiMarkCheck.Sample,
                "น้ำ ไม้ ป่า ก๊าซ จ๋า",
                "English item",
                "日本語の項目",
                "a long item that is cut with an ellipsis when the window is narrow enough to need it",
            });

            window.Text("log").AppendLine("<color=#9999BF>log</color> - events from this window appear here");
        }

        /// <summary>Queues a character reply that streams in, with a replay button when it is done.</summary>
        private static void Reply(ICwyChatView chat, string text)
        {
            var bubble = chat.Add(CwyChatRole.Character);
            bubble.Typing = true;
            bubble.Tag = text;
            Streams.Enqueue(new KeyValuePair<ICwyChatBubble, string>(bubble, text));
        }

        /// <summary>Once a frame: a few more characters of the oldest pending reply.</summary>
        private static void Stream()
        {
            if (_window == null || !_window.IsAlive)
            {
                Streams.Clear();
                return;
            }

            if (Streams.Count == 0)
            {
                return;
            }

            var current = Streams.Peek();
            var bubble = current.Key;
            var text = current.Value;
            if (!bubble.IsAlive)
            {
                Streams.Dequeue();
                _streamed = 0;
                return;
            }

            var take = Math.Min(CharactersPerFrame, text.Length - _streamed);
            if (take > 0)
            {
                bubble.Append(text.Substring(_streamed, take));
                _streamed += take;
            }

            if (_streamed >= text.Length)
            {
                bubble.Typing = false;
                bubble.AddButton("replay", "replay");
                Streams.Dequeue();
                _streamed = 0;
            }
        }
    }
}
