using System;
using System.Reflection;
using CwyLs.ModLoader;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// Makes an open todo panel show a write the bus just made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TodoListUI</c> instantiates one <c>TodoUI</c> per item when
    /// <c>Bulbul.FacilityTodo.OnClickButtonSelectTodoList</c> walks the data, and never re-reads
    /// afterwards - not even on <c>Activate()</c>. A write to the data alone is therefore invisible
    /// until the player switches lists, and stays invisible after closing and reopening the panel.
    /// </para>
    /// <para>
    /// The nudge calls that same public presenter method, which is exactly what a click on the
    /// list button does: it destroys the rows and rebuilds them from the data. It needs the
    /// <c>InteractableUI</c> belonging to the list button so the selected-button highlight is
    /// preserved; that field is private on <c>SelectTodoListUI</c> and is read reflectively.
    /// </para>
    /// <para>
    /// Going through <c>SelectTodoListUI.OnClickButtonSelectTodoList()</c> instead would be one
    /// reflective read cheaper but wrong: that method treats a second call within 0.5 s as a
    /// double-click and opens the title field for editing, which is not something a background
    /// write should ever do to the player.
    /// </para>
    /// </remarks>
    internal static class TodoUiNudge
    {
        private const string InteractableField = "_interactableUI";

        private static bool _unavailableLogged;

        /// <summary>
        /// Rebuilds the todo panel for <paramref name="listId"/>. Returns false when the room UI
        /// is not up (normal before the game starts) or the presenter could not be reached, in
        /// which case the data is still correct and the panel will catch up the next time the
        /// player selects a list.
        /// </summary>
        internal static bool Refresh(ulong listId)
        {
            try
            {
                var facility = Resolve<Game.FacilityTodo>();
                var listUi = Resolve<global::TodoListUI>();
                if (facility is null || listUi is null)
                {
                    return false;
                }

                var buttons = listUi.SelectTodoListUIList;
                if (buttons is null)
                {
                    return false;
                }

                foreach (var button in buttons)
                {
                    if (button is null || button.TodoListID != listId)
                    {
                        continue;
                    }

                    facility.OnClickButtonSelectTodoList(listId, InteractableOf(button));
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"DATA todo UI refresh failed (data is still correct): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static Game.InteractableUI? InteractableOf(Game.SelectTodoListUI button)
        {
            var field = typeof(Game.SelectTodoListUI)
                .GetField(InteractableField, BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                return field.GetValue(button) as Game.InteractableUI;
            }

            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                CwyLsModLoader.Log.LogWarning(
                    $"DATA SelectTodoListUI.{InteractableField} not found; the todo panel will still " +
                    "refresh but the selected-list highlight will be cleared");
            }

            return null;
        }

        private static T? Resolve<T>()
            where T : class
        {
            try
            {
                // RoomLifetimeScope.Resolve throws when the scope does not exist yet, which is the
                // normal state before the room scene is up.
                return Game.RoomLifetimeScope.Resolve<T>();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"DATA {typeof(T).Name} not resolvable: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Adds a note page through the game's own <c>NoteService</c> so the open notebook updates.
    /// </summary>
    /// <remarks>
    /// <c>SelectPageListUI</c> subscribes to <c>NoteService.OnAddPage</c> and instantiates the page
    /// entry itself, so unlike todos the note list needs no separate nudge - it just needs the page
    /// to be added through the service rather than straight into <c>NoteDataV2</c>.
    /// </remarks>
    internal static class NoteUiNudge
    {
        /// <summary>
        /// Adds the page through <c>NoteService</c>. Returns false when the service is not
        /// available (no room scene), leaving the caller to write the page directly.
        /// </summary>
        internal static bool TryAddThroughService(string title, string body)
        {
            global::NoteService? service;
            try
            {
                service = Game.RoomLifetimeScope.Resolve<global::NoteService>();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"DATA NoteService not resolvable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            if (service is null)
            {
                return false;
            }

            service.AddPage(title, body);
            return true;
        }
    }
}
