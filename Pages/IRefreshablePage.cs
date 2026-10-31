namespace Win11Toolbox.Pages;

/// <summary>
/// 页面自己知道「怎么重新读一遍数据」「搜索框在哪」，
/// 主窗口的 F5 / Ctrl+F 就不用按页面类型一个个 switch。
/// </summary>
public interface IRefreshablePage
{
    /// <summary>F5：重新读一遍（不重建页面，只重取数据）。</summary>
    void RefreshPage();

    /// <summary>Ctrl+F：把键盘焦点放进本页搜索框；没有搜索框就什么都不做。</summary>
    void FocusSearch();
}
