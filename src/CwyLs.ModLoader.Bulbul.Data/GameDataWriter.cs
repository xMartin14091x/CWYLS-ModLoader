using System;
using System.Collections.Generic;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Data;
using Game = global::Bulbul;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// What a write attempt did. Immutable; check <see cref="Ok"/> before trusting <see cref="Id"/>.
    /// </summary>
    public sealed class WriteResult
    {
        private WriteResult(bool ok, string detail, ulong id)
        {
            Ok = ok;
            Detail = detail;
            Id = id;
        }

        /// <summary>True when the player's data actually changed and was saved.</summary>
        public bool Ok { get; }

        /// <summary>Why it failed, or what it did. Never null.</summary>
        public string Detail { get; }

        /// <summary>Id of the row created or touched; 0 when nothing was.</summary>
        public ulong Id { get; }

        /// <summary>True when the live UI was told to rebuild from the data.</summary>
        public bool UiRefreshed { get; private init; }

        /// <summary>A successful write.</summary>
        internal static WriteResult Success(string detail, ulong id, bool uiRefreshed) =>
            new WriteResult(true, detail, id) { UiRefreshed = uiRefreshed };

        /// <summary>A refused or failed write.</summary>
        internal static WriteResult Failure(string detail) => new WriteResult(false, detail, 0UL);

        /// <summary>Diagnostic form.</summary>
        public override string ToString() => (Ok ? "ok: " : "failed: ") + Detail;
    }

    /// <summary>
    /// Write API v1: add a todo, complete a todo, add a note page. Reached through
    /// <see cref="GameData.Write"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Gated.</b> Every method refuses unless <c>Data.AllowWrites</c> is true in the plugin's
    /// .cfg. Reading the player's save data is harmless; writing it is not, and the player has no
    /// undo. Every accepted write is logged at Info with what it did.
    /// </para>
    /// <para>
    /// <b>Through the game's own methods.</b> Each write mutates the game's data objects using
    /// their own mutators - <c>TodoListData.AddTodo</c>, <c>TodoData.SetCompleteTodoDatetime</c>,
    /// <c>NoteService.AddPage</c> - and then calls the matching <c>SaveDataManager.SaveXxx</c>, so
    /// the save file stays exactly as the game would have written it and the bus's own Harmony
    /// postfix publishes the change. Nothing here writes a file itself.
    /// </para>
    /// <para>
    /// <b>UI refresh.</b> The room's todo panel (<c>TodoListUI</c>) is a pure view: it builds one
    /// <c>TodoUI</c> per item when its presenter, <c>Bulbul.FacilityTodo</c>, walks the data, and
    /// never re-reads afterwards. Writing to the data alone therefore leaves an open panel stale
    /// until the player switches lists. The nudge used here is the game's own list-selection path:
    /// <c>SelectTodoListUI.OnClickButtonSelectTodoList()</c>, which calls back into
    /// <c>FacilityTodo.OnClickButtonSelectTodoList</c>, which tears the rows down and rebuilds them
    /// from the data - exactly what the player's own click does. Notes need no nudge: their
    /// presenter listens to <c>NoteService.OnAddPage</c>, which <c>NoteService.AddPage</c> raises.
    /// </para>
    /// <para>
    /// <b>Main thread only.</b> These touch Unity objects.
    /// </para>
    /// </remarks>
    public sealed class GameDataWriter
    {
        internal GameDataWriter()
        {
        }

        /// <summary>True when <c>Data.AllowWrites</c> permits the methods below to run.</summary>
        public bool IsEnabled => GameData.Options.AllowWrites;

        /// <summary>
        /// Adds a todo item to a list.
        /// </summary>
        /// <param name="listId">
        /// The list to add to, from <see cref="GameData.Todos"/>. Pass 0 for the first list the
        /// game holds, which is the one the panel opens on.
        /// </param>
        /// <param name="text">The item's text. Must not be blank - the game refuses to let the
        /// player complete a blank todo, so creating one would make an item they cannot tick off.</param>
        /// <returns>The new item's id, or the reason it was refused.</returns>
        public WriteResult AddTodo(ulong listId, string text) =>
            Report("AddTodo", AddTodoCore(listId, text));

        private WriteResult AddTodoCore(ulong listId, string text)
        {
            if (!Allowed(out var refusal))
            {
                return refusal!;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return WriteResult.Failure("todo text is blank; the game cannot complete a blank todo");
            }

            var sdm = SaveDataAccess.Resolve();
            if (sdm?.TodoAllData?.TodoListDic is null)
            {
                return WriteResult.Failure("save data not loaded yet");
            }

            if (!TryGetList(sdm, listId, out var list) || list is null)
            {
                return WriteResult.Failure($"todo list {listId} not found");
            }

            try
            {
                var todo = new Game.TodoData { TodoText = text };

                // TodoListData.AddTodo appends to TodoOrderList and calls SaveTodoList itself,
                // which is what raises Changed(Todos) through our postfix.
                if (!list.AddTodo(todo))
                {
                    return WriteResult.Failure("the game rejected the new todo (duplicate id)");
                }

                var refreshed = TodoUiNudge.Refresh(list.UniqueID);
                CwyLsModLoader.Log.LogDebug(
                    $"DATA WRITE AddTodo list={list.UniqueID} id={todo.UniqueID} chars={text.Length} " +
                    $"uiRefreshed={refreshed}");

                return WriteResult.Success("todo added", todo.UniqueID, refreshed);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError($"DATA WRITE AddTodo failed: {ex.GetType().Name}: {ex.Message}");
                return WriteResult.Failure(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Marks a todo complete, wherever it lives.
        /// </summary>
        /// <param name="todoId">The item's id, from <see cref="GameData.Todos"/>.</param>
        /// <returns>The item's id, or the reason it was refused.</returns>
        /// <remarks>
        /// Reproduces what <c>FacilityTodo.CompleteTodo</c> does to the data - stamp the completion
        /// time, save the list, record a copy on today's calendar day and save that - minus the
        /// sound effect and the slide animation, which belong to the view.
        /// </remarks>
        public WriteResult CompleteTodo(ulong todoId) =>
            Report("CompleteTodo", CompleteTodoCore(todoId));

        private WriteResult CompleteTodoCore(ulong todoId)
        {
            if (!Allowed(out var refusal))
            {
                return refusal!;
            }

            var sdm = SaveDataAccess.Resolve();
            if (sdm?.TodoAllData is null)
            {
                return WriteResult.Failure("save data not loaded yet");
            }

            try
            {
                var todo = sdm.TodoAllData.GetTodoData(todoId, out var list);
                if (todo is null || list is null)
                {
                    return WriteResult.Failure($"todo {todoId} not found");
                }

                if (todo.CurrentState == global::TodoState.Complete)
                {
                    return WriteResult.Failure($"todo {todoId} is already complete");
                }

                var now = DateTime.Now;
                todo.SetCompleteTodoDatetime(now);
                sdm.SaveTodoList(list);

                // The game mirrors every completion onto the calendar day so the statistics screen
                // and the diary agree with the todo panel; skipping it would leave them disagreeing.
                var day = sdm.CalendarData?.GetDailyData(now);
                if (day?.CompleteTodoListDic != null)
                {
                    day.CompleteTodoListDic[todo.UniqueID] = todo.DeepCopy();
                    sdm.CalendarData!.SaveDateData(now);
                }

                var refreshed = TodoUiNudge.Refresh(list.UniqueID);
                CwyLsModLoader.Log.LogDebug(
                    $"DATA WRITE CompleteTodo list={list.UniqueID} id={todo.UniqueID} uiRefreshed={refreshed}");

                return WriteResult.Success("todo completed", todo.UniqueID, refreshed);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError($"DATA WRITE CompleteTodo failed: {ex.GetType().Name}: {ex.Message}");
                return WriteResult.Failure(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Adds a page to the notebook.
        /// </summary>
        /// <param name="title">The page title shown in the page list. Blank is allowed.</param>
        /// <param name="body">The page body.</param>
        /// <returns>The new page's id, or the reason it was refused.</returns>
        /// <remarks>
        /// Prefers the game's own <c>NoteService.AddPage(title, body)</c>, which raises
        /// <c>OnAddPage</c> and so updates an open notebook by itself. That method has a gap - it
        /// never saves the page index after setting the title - so the index is saved here
        /// afterwards; without that the title would be lost on restart. When the service cannot be
        /// resolved (the room scene is not up), the page is written straight through
        /// <c>NoteDataV2.AddPage</c> instead and the UI, which does not exist yet, needs no nudge.
        /// </remarks>
        public WriteResult AddNotePage(string title, string body) =>
            Report("AddNotePage", AddNotePageCore(title, body));

        private WriteResult AddNotePageCore(string title, string body)
        {
            if (!Allowed(out var refusal))
            {
                return refusal!;
            }

            var sdm = SaveDataAccess.Resolve();
            if (sdm?.NoteData is null)
            {
                return WriteResult.Failure("save data not loaded yet");
            }

            var safeTitle = title ?? string.Empty;
            var safeBody = body ?? string.Empty;

            try
            {
                var before = PageIds(sdm);

                if (NoteUiNudge.TryAddThroughService(safeTitle, safeBody))
                {
                    // NoteService.AddPage sets the title on the in-memory index but never saves it.
                    sdm.SaveNoteList();
                    var id = NewPageId(sdm, before);
                    CwyLsModLoader.Log.LogDebug(
                        $"DATA WRITE AddNotePage id={id} chars={safeBody.Length} via=NoteService uiRefreshed=True");
                    return WriteResult.Success("note page added", id, true);
                }

                var page = new Game.PageDataV2 { MainText = safeBody };
                if (!sdm.NoteData.AddPage(page))
                {
                    return WriteResult.Failure("the game rejected the new page (duplicate id)");
                }

                sdm.NoteData.SetTitleText(page.UniqueID, safeTitle);
                sdm.SaveNoteList();

                CwyLsModLoader.Log.LogDebug(
                    $"DATA WRITE AddNotePage id={page.UniqueID} chars={safeBody.Length} via=NoteDataV2 " +
                    "uiRefreshed=False");
                return WriteResult.Success("note page added", page.UniqueID, false);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError($"DATA WRITE AddNotePage failed: {ex.GetType().Name}: {ex.Message}");
                return WriteResult.Failure(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Publishes every write attempt - accepted, refused or failed - to the diagnostic stream,
        /// then hands the result back unchanged. A refusal is as interesting as a success when a
        /// modder is asking why nothing happened.
        /// </summary>
        private static WriteResult Report(string op, WriteResult result)
        {
            ConsoleEvents.Emit(
                GameData.TraceSys,
                "write",
                ConsoleTags.Data,
                op + (result.Ok ? " ok" : " refused"),
                "detail,uiRefreshed",
                ("op", op),
                ("ok", result.Ok),
                ("uiRefreshed", result.UiRefreshed),
                ("detail", result.Detail));

            return result;
        }

        private bool Allowed(out WriteResult? refusal)
        {
            if (!IsEnabled)
            {
                refusal = WriteResult.Failure("writes are disabled; set Data.AllowWrites = true to enable");
                return false;
            }

            refusal = null;
            return true;
        }

        private static bool TryGetList(Game.SaveDataManager sdm, ulong listId, out Game.TodoListData? list)
        {
            var dic = sdm.TodoAllData.TodoListDic;

            if (listId != 0UL)
            {
                return dic.TryGetValue(listId, out list);
            }

            foreach (var entry in dic)
            {
                list = entry.Value;
                return list != null;
            }

            list = null;
            return false;
        }

        private static HashSet<ulong> PageIds(Game.SaveDataManager sdm)
        {
            var ids = new HashSet<ulong>();
            var order = sdm.NoteData?.NoteList?.PageOrderList;
            if (order != null)
            {
                foreach (var id in order)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>
        /// The id the game just minted. <c>NoteService.AddPage</c> returns void, so the new page is
        /// identified by diffing the page order; 0 when it somehow did not appear.
        /// </summary>
        private static ulong NewPageId(Game.SaveDataManager sdm, HashSet<ulong> before)
        {
            var order = sdm.NoteData?.NoteList?.PageOrderList;
            if (order is null)
            {
                return 0UL;
            }

            for (var i = order.Count - 1; i >= 0; i--)
            {
                if (!before.Contains(order[i]))
                {
                    return order[i];
                }
            }

            return 0UL;
        }
    }
}
