using System;
using System.Collections.Generic;
using ValheimBakaLoader.Forms;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The host for a bridge with no window answers every question the way that keeps a
    /// server running unattended. These pin those answers, because a service that killed an
    /// orphaned world or left a stop request hanging would do it with nobody watching.
    /// </summary>
    public class HeadlessHostUiTests
    {
        [Fact]
        public void An_orphaned_server_is_adopted_not_killed()
        {
            var ui = new HeadlessHostUi(null);

            Assert.Equal(OrphanServerChoice.Adopt, ui.AskAdoptOrKill("Server Already Running", "A server is up."));
        }

        [Fact]
        public void A_close_with_servers_running_saves_and_shuts_down()
        {
            var ui = new HeadlessHostUi(null);

            Assert.True(ui.AskSaveAndShutDown("BakaLoader", "The server is still running", "It will be saved.", warn: true, "Save && shut down", null));
        }

        [Fact]
        public void Every_push_reaches_whoever_listens_in_order()
        {
            var ui = new HeadlessHostUi(null);
            var heard = new List<string>();
            ui.Posted += heard.Add;

            ui.PostJson("{\"event\":\"a\"}");
            ui.PostJson("{\"event\":\"b\"}");

            Assert.Equal(new[] { "{\"event\":\"a\"}", "{\"event\":\"b\"}" }, heard);
        }

        [Fact]
        public void The_host_is_alive_until_asked_to_close_and_says_so_once()
        {
            var ui = new HeadlessHostUi(null);
            var closes = 0;
            ui.CloseRequested += () => closes++;

            Assert.True(ui.IsAlive);
            ui.RequestClose();
            ui.RequestClose();

            Assert.False(ui.IsAlive);
            Assert.Equal(1, closes);
        }

        [Fact]
        public void Work_posted_to_a_host_with_no_thread_runs_here_and_now()
        {
            var ui = new HeadlessHostUi(null);
            var ran = false;

            ui.Post(() => ran = true);

            Assert.True(ran);
            Assert.True(ui.IsUiThread);
            Assert.Equal(7, ui.RunOnUiThread(() => 7));
        }

        [Fact]
        public void The_pickers_answer_cancel()
        {
            var ui = new HeadlessHostUi(null);

            Assert.Null(ui.PickFile("All files|*.*", "/tmp"));
            Assert.Null(ui.PickFolder("/tmp"));
        }
    }
}
