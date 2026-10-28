using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// The game members the data bus relies on beyond the save hook, as patch-target data, so a game
    /// update that breaks one is reported at startup (<c>PATCH TARGET MISSING</c>) rather than as a
    /// failed write or a silent gap in <see cref="GameData.Changed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three groups: the save-data load seams
    /// <see cref="SaveDataPatches"/> postfixes; the game methods <see cref="GameDataWriter"/> and the
    /// UI nudges call (plus the one private field they read by name); and the note and to-do
    /// observables <see cref="GameEventBridge"/> subscribes to. The <c>Save*</c> family itself is
    /// represented by <c>events.savedata.saveall</c>; <c>DeleteTodoList</c> stands for the
    /// <c>Delete*</c> half the data bus also hears through the shared hook.
    /// </para>
    /// <para>
    /// Signatures match game 1.17.3 and were checked against the shipped assembly through a
    /// metadata-only load.
    /// </para>
    /// </remarks>
    public static class DataPatchTargets
    {
        private const string GameAssembly = "Assembly-CSharp";
        private const string SaveManager = "Bulbul.SaveDataManager";

        /// <summary>Every seam, grouped as in the remarks.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            // --- Load seams (SaveDataPatches) ---------------------------------------------------
            // Async; its postfix captures the instance.
            new PatchTarget(
                "data.savedata.load", GameAssembly, SaveManager,
                MemberKind.Method, "Load",
                expectedSignature: "UniTask Load()"),

            // Called once from Load(): the "save data is in memory" signal.
            new PatchTarget(
                "data.savedata.loadallplatformsetting", GameAssembly, SaveManager,
                MemberKind.Method, "LoadAllPlatformSetting",
                expectedSignature: "void LoadAllPlatformSetting()"),

            // Stands for the Delete* family on the shared save hook.
            new PatchTarget(
                "data.savedata.deletetodolist", GameAssembly, SaveManager,
                MemberKind.Method, "DeleteTodoList",
                new[] { "System.UInt64" },
                expectedSignature: "void DeleteTodoList(ulong)"),

            // --- Write API: AddTodo -----------------------------------------------------------
            // Appends and saves the list itself.
            new PatchTarget(
                "data.write.todolist.addtodo", GameAssembly, "Bulbul.TodoListData",
                MemberKind.Method, "AddTodo",
                new[] { "Bulbul.TodoData" },
                expectedSignature: "bool AddTodo(TodoData)"),

            // --- Write API: CompleteTodo ------------------------------------------------------
            new PatchTarget(
                "data.write.todoall.gettododata", GameAssembly, "Bulbul.TodoAllData",
                MemberKind.Method, "GetTodoData",
                new[] { "System.UInt64", "Bulbul.TodoListData&" },
                expectedSignature: "TodoData GetTodoData(ulong, TodoListData&)"),

            new PatchTarget(
                "data.write.todo.setcompletetododatetime", GameAssembly, "Bulbul.TodoData",
                MemberKind.Method, "SetCompleteTodoDatetime",
                new[] { "System.Nullable<System.DateTime>" },
                expectedSignature: "void SetCompleteTodoDatetime(Nullable<DateTime>)"),

            // The copy recorded on the calendar day.
            new PatchTarget(
                "data.write.todo.deepcopy", GameAssembly, "Bulbul.TodoData",
                MemberKind.Method, "DeepCopy",
                expectedSignature: "TodoData DeepCopy()"),

            new PatchTarget(
                "data.write.savedata.savetodolist", GameAssembly, SaveManager,
                MemberKind.Method, "SaveTodoList",
                new[] { "Bulbul.TodoListData" },
                expectedSignature: "void SaveTodoList(TodoListData)"),

            new PatchTarget(
                "data.write.calendar.getdailydata", GameAssembly, "Bulbul.CalendarData",
                MemberKind.Method, "GetDailyData",
                new[] { "System.DateTime" },
                expectedSignature: "CalendarDateData GetDailyData(DateTime)"),

            new PatchTarget(
                "data.write.calendar.savedatedata", GameAssembly, "Bulbul.CalendarData",
                MemberKind.Method, "SaveDateData",
                new[] { "System.DateTime" },
                expectedSignature: "void SaveDateData(DateTime)"),

            // --- Write API: AddNotePage -------------------------------------------------------
            // NoteService is in the global namespace. Raises OnAddPage.
            new PatchTarget(
                "data.write.noteservice.addpage", GameAssembly, "NoteService",
                MemberKind.Method, "AddPage",
                new[] { "System.String", "System.String" },
                expectedSignature: "void AddPage(string, string)"),

            // Saves the page index NoteService.AddPage leaves unsaved.
            new PatchTarget(
                "data.write.savedata.savenotelist", GameAssembly, SaveManager,
                MemberKind.Method, "SaveNoteList",
                expectedSignature: "void SaveNoteList()"),

            // The fallback when the room (and NoteService) is not up.
            new PatchTarget(
                "data.write.notedata.addpage", GameAssembly, "Bulbul.NoteDataV2",
                MemberKind.Method, "AddPage",
                new[] { "Bulbul.PageDataV2" },
                expectedSignature: "bool AddPage(PageDataV2)"),

            new PatchTarget(
                "data.write.notedata.settitletext", GameAssembly, "Bulbul.NoteDataV2",
                MemberKind.Method, "SetTitleText",
                new[] { "System.UInt64", "System.String" },
                expectedSignature: "bool SetTitleText(ulong, string)"),

            // --- Write API: the to-do panel refresh (UiNudges) ------------------------------------
            new PatchTarget(
                "data.write.facilitytodo.selectlist", GameAssembly, "Bulbul.FacilityTodo",
                MemberKind.Method, "OnClickButtonSelectTodoList",
                new[] { "System.UInt64", "Bulbul.InteractableUI" },
                expectedSignature: "void OnClickButtonSelectTodoList(ulong, InteractableUI)"),

            // Private, read by name (TodoUiNudge).
            new PatchTarget(
                "data.write.selecttodolistui.interactable", GameAssembly, "Bulbul.SelectTodoListUI",
                MemberKind.Field, "_interactableUI",
                expectedSignature: "InteractableUI _interactableUI"),

            // --- Observables the data bus subscribes to itself (GameEventBridge) ----------------
            new PatchTarget(
                "data.note.onaddpage", GameAssembly, "NoteService",
                MemberKind.Property, "OnAddPage",
                expectedSignature: "Observable<PageDataV2> OnAddPage { get; }"),

            new PatchTarget(
                "data.note.onremovepage", GameAssembly, "NoteService",
                MemberKind.Property, "OnRemovePage",
                expectedSignature: "Observable<ulong> OnRemovePage { get; }"),

            new PatchTarget(
                "data.note.onendeditnote", GameAssembly, "NoteService",
                MemberKind.Property, "OnEndEditNote",
                expectedSignature: "Observable<Unit> OnEndEditNote { get; }"),

            new PatchTarget(
                "data.todo.onaddtodo", GameAssembly, "Bulbul.FacilityTodo",
                MemberKind.Property, "OnAddTodo",
                expectedSignature: "Observable<TodoData> OnAddTodo { get; }"),

            new PatchTarget(
                "data.todo.onremovetodo", GameAssembly, "Bulbul.FacilityTodo",
                MemberKind.Property, "OnRemoveTodo",
                expectedSignature: "Observable<TodoData> OnRemoveTodo { get; }"),

            new PatchTarget(
                "data.todo.oncompletetodo", GameAssembly, "Bulbul.FacilityTodo",
                MemberKind.Property, "OnCompleteTodo",
                expectedSignature: "Observable<TodoData> OnCompleteTodo { get; }"),

            new PatchTarget(
                "data.todo.onuncompletetodo", GameAssembly, "Bulbul.FacilityTodo",
                MemberKind.Property, "OnUncompleteTodo",
                expectedSignature: "Observable<TodoData> OnUncompleteTodo { get; }"),
        };
    }
}
