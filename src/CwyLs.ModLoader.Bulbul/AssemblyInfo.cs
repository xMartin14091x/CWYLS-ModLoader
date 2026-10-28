using System.Runtime.CompilerServices;

// The data bus (CwyLs.ModLoader.Bulbul.Data) listens to the adapter's single save hook
// (Events/SaveDataHook) and to the event bridge's observable signals (Events/EventBridge) instead
// of patching and subscribing to the same game members a second time. Both are framework plumbing,
// not mod API, so they stay internal and only the sibling data plugin may see them.
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Bulbul.Data")]
