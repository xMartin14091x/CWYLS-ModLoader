using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-ACT-CORE: the UiClick deny list (shop, account linking, cloud sync, SNS/browser) against the
    /// game's real RoomScene paths (game 1.17.3), and the click result's action mapping.
    /// </summary>
    public class ActionsUiClickPolicyTests
    {
        private const string Setting = "Paremt/PCPlatform/Canvas/UI/ChangeOrderObjects/UI_FacilitySetting/";
        private const string Account = Setting + "PC_Account_Setting/ContentArea/Scroll View/Viewport/Content/";
        private const string Environment = "Paremt/PCPlatform/Canvas/UI/ChangeOrderObjects/UI_FacilityEnviroment/";

        [Theory]
        [InlineData("UI_FacilitySetting", "ui|facility|setting")]
        [InlineData("SNSButton", "sns|button")]
        [InlineData("InquiryIDButton", "inquiry|id|button")]
        [InlineData("PCPlatform", "pc|platform")]
        [InlineData("CloudyButton", "cloudy|button")]
        [InlineData("SaveDataUploadIntervalPulldown", "save|data|upload|interval|pulldown")]
        [InlineData("ChangeCook(B)Button", "change|cook|b|button")]
        [InlineData("CalenderDayButton (30)", "calender|day|button|30")]
        [InlineData("Scroll View", "scroll|view")]
        [InlineData("", "")]
        public void Names_split_into_lower_case_words(string name, string expected) =>
            Assert.Equal(expected, string.Join("|", UiClickPolicy.Tokens(name)));

        [Theory]
        [InlineData(Setting + "SNS/X", UiClickPolicy.SnsBrowser, "sns")]
        [InlineData(Setting + "SNS/Discord", UiClickPolicy.SnsBrowser, "sns")]
        [InlineData(Setting + "General/ScrollView/Viewport/Content/FAQ/TitleText/FAQButton", UiClickPolicy.SnsBrowser, "faq")]
        [InlineData(Account + "TermsAndPrivacyPolicy/Terms", UiClickPolicy.Account, "account")]
        [InlineData(Account + "GoogleAccount/GoogleSignInButton/LinkButton", UiClickPolicy.Account, "account")]
        [InlineData(Setting + "General/ScrollView/Viewport/Content/InquiryID/TitleText/InquiryIDButton", UiClickPolicy.Account, "inquiry")]
        [InlineData(Setting + "General/ScrollView/Viewport/Content/SaveDataUploadInterval/SaveDataUploadIntervalPulldown", UiClickPolicy.CloudSync, "upload")]
        [InlineData(Setting + "SelectSettingButtons/SettingTabButton Account", UiClickPolicy.Account, "account")]
        [InlineData(Environment + "PurchasePopover/PurchaseButton", UiClickPolicy.Shop, "purchase")]
        [InlineData(Environment + "EnviromentScrollView/Viewport/Content/GridLayout/ChangeWindBellButton/ShopButton", UiClickPolicy.Shop, "shop")]
        [InlineData("Paremt/PCPlatform/Canvas/UI/MostFrontArea/AccountTransferDialog_PC", UiClickPolicy.Account, "account")]
        public void Protected_controls_of_the_room_are_refused(string path, string category, string word)
        {
            var verdict = UiClickPolicy.CheckPath(path);

            Assert.True(verdict.IsDenied);
            Assert.Equal(category, verdict.Category);
            Assert.Equal(word, verdict.Rule);
            Assert.StartsWith(category + " (\"" + word + "\" in segment \"", verdict.Reason);
        }

        [Theory]
        [InlineData(Environment + "EnviromentOther/WeatherChangeButtons/CloudyButton")]
        [InlineData(Environment + "EnviromentScrollView/Viewport/Content/GridLayout/ChangeWindBellButton")]
        [InlineData("Paremt/PCPlatform/Canvas/UI/ChangeOrderObjects/MusicPlayList/Contents/LocalMusicImport/FileImportButton")]
        [InlineData("Paremt/PCPlatform/Canvas/UI/UI_FacilityPomodoro/PomodoroTimer/PomodoroSetting/PlayButtonUI/PlayButton")]
        [InlineData(Setting + "Credits/ScrollView/Viewport/Content/License")]
        [InlineData("")]
        [InlineData(null)]
        public void Ordinary_controls_are_allowed(string? path)
        {
            var verdict = UiClickPolicy.CheckPath(path);

            Assert.False(verdict.IsDenied);
            Assert.Same(UiClickVerdict.Allowed, verdict);
            Assert.Equal("allowed", verdict.Reason);
        }

        [Fact]
        public void Denied_component_types_match_exactly()
        {
            var denied = new Dictionary<string, string> { ["Bulbul.PurchasePopover"] = UiClickPolicy.Shop };

            var verdict = UiClickPolicy.CheckTypes(new[] { "UnityEngine.RectTransform", null, "Bulbul.PurchasePopover" }, denied);
            Assert.True(verdict.IsDenied);
            Assert.Equal(UiClickPolicy.Shop, verdict.Category);
            Assert.Equal("component Bulbul.PurchasePopover", verdict.Where);

            Assert.False(UiClickPolicy.CheckTypes(new[] { "Bulbul.PurchasePopoverExtra", "UnityEngine.UI.Button" }, denied).IsDenied);
            Assert.False(UiClickPolicy.CheckTypes(null, denied).IsDenied);
            Assert.False(UiClickPolicy.CheckTypes(new[] { "Bulbul.PurchasePopover" }, null).IsDenied);
        }

        [Fact]
        public void Every_category_has_words_and_the_list_is_sorted()
        {
            var words = UiClickPolicy.DeniedWords;
            var categories = new HashSet<string>();
            for (var i = 0; i < words.Count; i++)
            {
                categories.Add(words[i].Value);
                if (i > 0)
                {
                    Assert.True(string.CompareOrdinal(words[i - 1].Key, words[i].Key) < 0);
                }
            }

            Assert.Equal(new HashSet<string> { UiClickPolicy.Shop, UiClickPolicy.Account, UiClickPolicy.CloudSync, UiClickPolicy.SnsBrowser }, categories);
        }

        [Fact]
        public void A_click_result_maps_onto_an_action_result()
        {
            var clicked = new UiClickResult(UiClickStatus.Clicked, "A/B", "A/B", null);
            var ok = clicked.ToActionResult("pressed play");
            Assert.Equal(ActionStatus.Ok, ok.Status);
            Assert.Equal(ActionPath.UiClick, ok.Path);
            Assert.Equal("pressed play", ok.Message);
            Assert.Equal("clicked A/B", clicked.ToActionResult(" ").Message);

            var denied = new UiClickResult(UiClickStatus.Denied, "A/SNS/X", string.Empty, "sns/browser (\"sns\" in segment \"SNS\")").ToActionResult("x");
            Assert.Equal(ActionStatus.Denied, denied.Status);
            Assert.Contains("refused", denied.Message);

            Assert.Equal(ActionStatus.Failed, new UiClickResult(UiClickStatus.NotFound, "A/B", null, "no active object").ToActionResult("x").Status);
            Assert.Equal(ActionStatus.Failed, new UiClickResult(UiClickStatus.Probed, "A/B", "A/B", null).ToActionResult("x").Status);
            Assert.Equal("NotInteractable A/B: disabled", new UiClickResult(UiClickStatus.NotInteractable, "A/B", "A/B", "disabled").ToString());
            Assert.Throws<ArgumentException>(() => new UiClickResult(UiClickStatus.Failed, "A", null, " "));
        }
    }
}
