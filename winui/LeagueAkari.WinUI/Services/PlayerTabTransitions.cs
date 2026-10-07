namespace LeagueAkari.WinUI.Services;

public static class PlayerTabTransitions
{
    // 原版关闭任意页后选择它右侧的页；末尾关闭时选择左侧。
    public static int SelectedIndexAfterClose(int removedIndex, int remainingCount) =>
        remainingCount == 0 ? -1 : Math.Min(removedIndex, remainingCount - 1);

    public static bool CanCloseOthers(int tabIndex, int count) => tabIndex >= 0 && count > 1;

    public static bool CanCloseRight(int tabIndex, int count) => tabIndex >= 0 && tabIndex < count - 1;
}
