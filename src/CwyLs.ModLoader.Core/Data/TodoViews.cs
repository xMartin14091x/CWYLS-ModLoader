using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>Completion state of a todo item; mirrors the game's <c>TodoState</c>.</summary>
    public enum TodoViewState
    {
        /// <summary>Not finished.</summary>
        Working = 0,

        /// <summary>Finished; <see cref="TodoView.CompletedAt"/> says when.</summary>
        Complete = 1,
    }

    /// <summary>
    /// One todo item, copied out of the game's <c>Bulbul.TodoData</c>. Immutable: mutating a live
    /// game object from a mod would bypass the game's own save calls, so the bus never hands one out.
    /// </summary>
    public sealed class TodoView
    {
        /// <summary>Game-assigned id (a <c>DateTime.Now.Ticks</c> value, so it needs 64 bits).</summary>
        public ulong Id { get; init; }

        /// <summary>The text the player typed. May be empty for a freshly added, unedited row.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Whether the item is done.</summary>
        public TodoViewState State { get; init; }

        /// <summary>When it was completed, or <c>null</c> while it is still open.</summary>
        public DateTime? CompletedAt { get; init; }

        /// <summary>Due date the player picked, or <c>null</c> when none is set.</summary>
        public DateTime? ExpiresAt { get; init; }
    }

    /// <summary>
    /// One todo list (the game calls them "pages" in the pull-down), with its items already in the
    /// player's chosen order.
    /// </summary>
    public sealed class TodoListView
    {
        /// <summary>Game-assigned list id.</summary>
        public ulong Id { get; init; }

        /// <summary>The list's title. Empty when the player never named it.</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>Items, in the player's display order (the game's <c>TodoOrderList</c>).</summary>
        public IReadOnlyList<TodoView> Items { get; init; } = Array.Empty<TodoView>();

        /// <summary>Number of items whose state is <see cref="TodoViewState.Complete"/>.</summary>
        public int CompletedCount
        {
            get
            {
                var done = 0;
                foreach (var item in Items)
                {
                    if (item.State == TodoViewState.Complete)
                    {
                        done++;
                    }
                }

                return done;
            }
        }

        /// <summary>Number of items still open.</summary>
        public int OpenCount => Items.Count - CompletedCount;
    }
}
