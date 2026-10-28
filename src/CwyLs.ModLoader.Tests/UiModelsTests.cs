using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// S-UI part models: what mods change (from any thread) and what the views draw; events run
    /// through the owner's runner (the mod's attribution), and a window model copies its spec.
    /// </summary>
    public class UiModelsTests
    {
        private readonly List<string> _ran = new List<string>();

        private bool Run(string label, Action handler)
        {
            _ran.Add(label);
            handler();
            return true;
        }

        // ---- text --------------------------------------------------------------------------------

        [Fact]
        public void Text_lines_append_split_and_trim()
        {
            var text = new TextViewModel(new CwyTextSpec("log") { MaxLines = 3 }, Run);
            var v0 = text.Version;
            text.AppendLine("one");
            text.AppendLine("two\r\nthree");
            Assert.Equal(3, text.Count);
            Assert.True(text.Version > v0);

            var first = text.Take();
            Assert.Equal(new[] { "one", "two", "three" }, first.Lines);
            Assert.Equal(3, first.Appended);

            text.AppendLine("four");
            var second = text.Take();
            Assert.Equal(new[] { "two", "three", "four" }, second.Lines);
            Assert.True(second.ContentVersion > first.ContentVersion);
            Assert.Equal(1, second.Appended);
            Assert.Equal(0, text.Take().Appended);
        }

        [Fact]
        public void Text_set_lines_and_clear_replace_everything()
        {
            var text = new TextViewModel(new CwyTextSpec("log"), Run);
            text.AppendLine("x");
            text.Take();
            text.SetLines(new[] { "a", null!, "b" });
            var snapshot = text.Take();
            Assert.Equal(new[] { "a", string.Empty, "b" }, snapshot.Lines);
            Assert.Equal(0, snapshot.Appended);
            text.Clear();
            Assert.Equal(0, text.Count);
        }

        [Fact]
        public void Text_scroll_requests_are_taken_once()
        {
            var text = new TextViewModel(new CwyTextSpec("log"), Run);
            Assert.Equal(ScrollRequest.None, text.TakeScrollRequest());
            text.ScrollToStart();
            Assert.Equal(ScrollRequest.Start, text.TakeScrollRequest());
            Assert.Equal(ScrollRequest.None, text.TakeScrollRequest());
            text.ScrollToEnd();
            Assert.Equal(ScrollRequest.End, text.TakeScrollRequest());
            Assert.True(text.IsFollowing);
            text.ReportFollowing(false);
            Assert.False(text.IsFollowing);
        }

        // ---- list --------------------------------------------------------------------------------

        [Fact]
        public void List_click_selects_and_raises_through_the_runner()
        {
            var list = new ListViewModel(new CwyListSpec("items"), Run);
            var clicked = -1;
            list.ItemClicked += index => clicked = index;
            list.SetItems(new[] { "a", "b\nc" });
            Assert.Equal("b c", list.Items[1]);

            list.Click(1);
            Assert.Equal(1, clicked);
            Assert.Equal(1, list.Selected);
            Assert.Contains("items ItemClicked", _ran);

            list.Click(5);
            Assert.Equal(1, clicked);
        }

        [Fact]
        public void List_new_items_clear_the_selection_and_bad_selections_are_none()
        {
            var list = new ListViewModel(new CwyListSpec("items"), Run);
            list.SetItems(new[] { "a", "b" });
            list.Selected = 1;
            Assert.Equal(1, list.Selected);
            list.Selected = 7;
            Assert.Equal(-1, list.Selected);
            list.Selected = 0;
            list.SetItems(new[] { "c" });
            Assert.Equal(-1, list.Selected);
            Assert.Empty(_ran);
        }

        [Fact]
        public void List_scroll_to_is_a_one_shot_request()
        {
            var list = new ListViewModel(new CwyListSpec("items"), Run);
            list.ScrollTo(-3);
            Assert.Equal(-1, list.TakeScrollRequest());
            list.ScrollTo(4);
            Assert.Equal(4, list.TakeScrollRequest());
            Assert.Equal(-1, list.TakeScrollRequest());
        }

        // ---- input -------------------------------------------------------------------------------

        [Fact]
        public void Input_text_set_by_the_mod_wins_until_the_box_shows_it()
        {
            var input = new InputBoxModel(new CwyInputSpec("say") { Lines = 3, Placeholder = "type" }, Run);
            Assert.Equal(3, input.Lines);
            Assert.Equal("type", input.Placeholder);

            input.Report("typed", focused: true, composing: false);
            Assert.Equal("typed", input.Text);
            Assert.True(input.IsFocused);

            input.Text = "from mod";
            input.Report("typed more", true, false);
            Assert.Equal("from mod", input.Text);

            Assert.True(input.TakePendingText(out var pending));
            Assert.Equal("from mod", pending);
            Assert.False(input.TakePendingText(out _));
            input.Report("from mod!", true, true);
            Assert.Equal("from mod!", input.Text);
            Assert.True(input.IsComposing);
        }

        [Fact]
        public void Input_submit_clears_the_box_and_runs_the_handler()
        {
            var input = new InputBoxModel(new CwyInputSpec("say"), Run);
            string? sent = null;
            input.Submitted += text => sent = text;
            input.Report("สวัสดี", true, false);
            input.RaiseSubmitted("สวัสดี");
            Assert.Equal("สวัสดี", sent);
            Assert.Equal(string.Empty, input.Text);
            Assert.True(input.TakePendingText(out var cleared));
            Assert.Equal(string.Empty, cleared);
            Assert.Contains("say Submitted", _ran);
        }

        [Fact]
        public void Input_keeps_its_text_when_asked_to()
        {
            var input = new InputBoxModel(new CwyInputSpec("say") { ClearOnSubmit = false }, Run);
            input.Report("keep", true, false);
            input.RaiseSubmitted("keep");
            Assert.Equal("keep", input.Text);
            Assert.False(input.TakePendingText(out _));
        }

        [Fact]
        public void Input_focus_requests_and_enabled()
        {
            var input = new InputBoxModel(new CwyInputSpec("say"), Run);
            Assert.Equal(FocusRequest.None, input.TakeFocusRequest());
            input.Focus();
            input.Release();
            Assert.Equal(FocusRequest.Release, input.TakeFocusRequest());
            input.Focus();
            Assert.Equal(FocusRequest.Focus, input.TakeFocusRequest());
            var v = input.Version;
            input.Enabled = false;
            Assert.False(input.Enabled);
            Assert.True(input.Version > v);
            v = input.Version;
            input.Enabled = false;
            Assert.Equal(v, input.Version);
        }

        // ---- buttons and tabs --------------------------------------------------------------------

        [Fact]
        public void Buttons_relabel_hide_and_click()
        {
            var spec = new CwyButtonsSpec("actions");
            spec.Buttons.Add(new CwyButtonSpec("send", "Send"));
            spec.Buttons.Add(new CwyButtonSpec("clear", "Clear"));
            var row = new ButtonRowModel(spec, Run);
            string? clicked = null;
            row.Clicked += id => clicked = id;

            Assert.True(row.SetLabel("SEND", "ส่ง"));
            Assert.True(row.SetButtonVisible("clear", false));
            Assert.False(row.SetLabel("nope", "x"));
            var states = row.Snapshot();
            Assert.Equal("ส่ง", states[0].Label);
            Assert.False(states[1].Visible);
            Assert.Equal("ส่ง", row.Buttons[0].Label);

            row.Click("send");
            Assert.Equal("send", clicked);
        }

        [Fact]
        public void Tabs_raise_only_for_the_players_pick()
        {
            var spec = new CwyTabsSpec("tabs") { Selected = 1 };
            spec.Labels.Add("Chat");
            spec.Labels.Add("Log");
            var tabs = new TabStripModel(spec, Run);
            var picked = new List<int>();
            tabs.SelectedChanged += picked.Add;

            Assert.Equal(1, tabs.Selected);
            tabs.Selected = 0;
            tabs.Selected = 9;
            Assert.Equal(0, tabs.Selected);
            Assert.Empty(picked);

            tabs.Pick(1);
            tabs.Pick(1);
            tabs.Pick(-1);
            Assert.Equal(new[] { 1 }, picked);
        }

        [Fact]
        public void Visible_changes_bump_the_version_once()
        {
            var text = new TextViewModel(new CwyTextSpec("log"), Run);
            var v = text.Version;
            text.Visible = false;
            text.Visible = false;
            Assert.Equal(v + 1, text.Version);
            Assert.False(text.Visible);
        }

        [Fact]
        public void A_handler_without_subscribers_does_not_go_through_the_runner()
        {
            var list = new ListViewModel(new CwyListSpec("items"), Run);
            list.SetItems(new[] { "a" });
            list.Click(0);
            Assert.Empty(_ran);
        }

        // ---- window model ------------------------------------------------------------------------

        private static CwyWindowSpec TabbedSpec()
        {
            var spec = new CwyWindowSpec("demo") { Title = "Demo", Width = 700f, CloseOnEscape = false };
            var strip = new CwyTabsSpec("tabs");
            strip.Labels.Add("Chat");
            strip.Labels.Add("Log");
            spec.Parts.Add(strip);
            spec.Parts.Add(new CwyChatSpec("chat") { Tab = 0 });
            spec.Parts.Add(new CwyInputSpec("say") { Tab = 0 });
            spec.Parts.Add(new CwyTextSpec("log") { Tab = 1 });
            return spec;
        }

        [Fact]
        public void A_window_model_copies_its_spec()
        {
            var spec = TabbedSpec();
            var model = new WindowModel("my.mod", spec, Run);
            spec.Title = "changed later";
            spec.Parts.Clear();

            Assert.Equal("Demo", model.Title);
            Assert.Equal("my.mod/demo", model.Key);
            Assert.Equal("CWY.Mod.my.mod.demo", model.RootName);
            Assert.Equal(4, model.Parts.Count);
            Assert.False(model.CloseOnEscape);
            Assert.True(model.HideOnClose);
            Assert.NotNull(model.TabStrip);
        }

        [Fact]
        public void Tabs_decide_which_parts_are_laid_out()
        {
            var model = new WindowModel("my.mod", TabbedSpec(), Run);
            var slots = model.Layout(500f);
            Assert.True(slots[0].Visible);
            Assert.True(slots[1].Visible);
            Assert.True(slots[2].Visible);
            Assert.False(slots[3].Visible);

            model.TabStrip!.Selected = 1;
            slots = model.Layout(500f);
            Assert.False(slots[1].Visible);
            Assert.False(slots[2].Visible);
            Assert.True(slots[3].Visible);
            Assert.Equal(500f - CwyWindowSpecRules.TabsHeight - StackLayout.Gap, slots[3].Height);

            model.Text("log").Visible = false;
            Assert.False(model.Layout(500f)[3].Visible);
        }

        [Fact]
        public void Parts_are_found_by_id_and_kind_with_a_clear_error()
        {
            var model = new WindowModel("my.mod", TabbedSpec(), Run);
            Assert.Same(model.Parts[1], model.Chat("CHAT"));
            Assert.Same(model.Parts[2], model.Input("say"));
            var error = Assert.Throws<ArgumentException>(() => model.List("chat"));
            Assert.Contains("has no list part 'chat'", error.Message);
            Assert.Contains("chat:chat", error.Message);
        }

        [Fact]
        public void An_invalid_spec_cannot_become_a_window()
        {
            var spec = new CwyWindowSpec("bad id");
            var error = Assert.Throws<ArgumentException>(() => new WindowModel("my.mod", spec, Run));
            Assert.Contains("window id", error.Message);
            Assert.Throws<ArgumentException>(() => new WindowModel(string.Empty, TabbedSpec(), Run));
        }
    }
}
