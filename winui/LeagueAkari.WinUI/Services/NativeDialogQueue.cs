using System.Runtime.CompilerServices;
namespace LeagueAkari.WinUI.Services;

/// <summary>Dialogs share one slot per native visual root; separate windows remain independent.</summary>
public sealed class NativeDialogQueue
{
    private readonly ConditionalWeakTable<object, SemaphoreSlim> _slots = new();
    public async Task<T?> RunAsync<T>(object root, Func<Task<T>> show, Func<bool>? canShow = null) where T : struct
    {
        var slot = _slots.GetValue(root, static _ => new SemaphoreSlim(1, 1));
        await slot.WaitAsync();
        try
        {
            if (canShow?.Invoke() == false) return null;
            return await show();
        }
        finally { slot.Release(); }
    }
}
