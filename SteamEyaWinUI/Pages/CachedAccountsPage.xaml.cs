using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class CachedAccountsPage : Page
{
    private readonly ObservableCollection<CachedSteamLoginAccount> _viewItems = [];
    private IReadOnlyList<CachedSteamLoginAccount> _sourceItems = [];
    private bool _isDialogFlowActive;

    /// <summary>
    /// 批量勾选集（账号 CacheKey）。与 ListView 的单选（详情焦点）解耦：勾选卡片左上角复选框进入此集，
    /// 驱动卡片黑框+对勾与底部批量操作栏。按键存储以便跨列表重建（换新实例）保留勾选。与历史账号页一致。
    /// </summary>
    private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);

    public CachedAccountsPage()
    {
        InitializeComponent();
        CachedAccountList.ItemsSource = _viewItems;
        AppState.BusyChanged += _ => UpdateControlsEnabled();
        Reload();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload(GetSelectedKey());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 悬停时被导航走，PointerExited 可能不触发；清掉残留悬停态，避免回来后空心勾选圈残留。
        foreach (var account in _sourceItems)
        {
            account.IsPointerOver = false;
        }
    }

    private void Reload(string? selectKey = null)
    {
        _sourceItems = AppState.LoginService.GetCachedLoginAccounts();
        RebuildView(selectKey);
    }

    private void RebuildView(string? selectKey)
    {
        var source = _sourceItems;
        var filter = CachedSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(account => Matches(account, filter)).ToList();

        // 批量勾选集按 CacheKey 跨重建保留：先剔除已不存在的账号，再把勾选状态套用到（可能是新的）实例。
        var liveKeys = source.Select(account => account.CacheKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedKeys.IntersectWith(liveKeys);
        foreach (var account in source)
        {
            account.IsSelected = _checkedKeys.Contains(account.CacheKey);
        }

        // 记住当前单选（详情焦点）的账号键，重建后恢复——刷新等延迟操作不应丢失当前查看的账号。
        var activeKey = !string.IsNullOrWhiteSpace(selectKey)
            ? selectKey
            : CachedAccountList.SelectedItem is CachedSteamLoginAccount current ? current.CacheKey : null;

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var active = activeKey is null
            ? null
            : _viewItems.FirstOrDefault(account =>
                string.Equals(account.CacheKey, activeKey, StringComparison.OrdinalIgnoreCase));
        CachedAccountList.SelectedItem = active ?? _viewItems.FirstOrDefault();

        var hasAny = source.Count > 0;
        CachedEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CachedEmptyText.Text = hasAny ? "没有匹配的缓存账号" : "暂无缓存账号";
        CachedEmptyHintText.Text = hasAny
            ? "换个关键词试试，或清空搜索框。"
            : "先正常登录 Steam，再使用 EYA 登录，原 Steam 账号会自动缓存。";
        CachedSummaryText.Text = hasAny
            ? $"共 {source.Count} 个缓存账号，使用 EYA 登录前检测到的原 Steam 登录账号会记录在这里。"
            : "使用 EYA 登录前检测到的原 Steam 登录账号会记录在这里。";

        UpdateBatchBar();
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private static bool Matches(CachedSteamLoginAccount account, string filter)
    {
        return Contains(account.AccountName, filter) ||
            Contains(account.PersonaName, filter) ||
            Contains(account.SteamId, filter);
    }

    private static bool Contains(string? value, string filter)
    {
        return !string.IsNullOrEmpty(value) &&
            value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void CachedSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RebuildView(GetSelectedKey());
        }
    }

    private async void RefreshCachedButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetBusy(true);
        AppState.ShowStatus("正在刷新缓存账号资料...", InfoBarSeverity.Informational);

        try
        {
            var refreshed = await AppState.LoginService.RefreshCachedLoginProfilesAsync(_sourceItems);
            Reload(GetSelectedKey());
            AppState.ShowStatus($"缓存账号已刷新，已同步 {refreshed} 个头像/资料。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Reload(GetSelectedKey());
            AppState.ShowStatus($"刷新缓存账号资料失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private void CachedAccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private async void RestoreSelectedCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            AppState.ShowStatus("请先选择要恢复的缓存账号。", InfoBarSeverity.Error);
            return;
        }

        AppState.SetBusy(true);
        AppState.ShowStatus($"正在恢复缓存账号 {account.AccountTitle}...", InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            AppState.ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var restored = await Task.Run(() => AppState.LoginService.RestoreCachedLogin(account, progress));
            Reload(restored.CacheKey);
            AppState.ShowStatus(
                $"已请求恢复缓存账号：{restored.AccountName}（SteamID: {restored.SteamId}）。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("恢复缓存账号失败。", ex);
            AppState.ShowStatus($"{ex.Message}（诊断日志：{AppLog.LogFilePath}）", InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void ClearCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (_sourceItems.Count == 0)
        {
            AppState.ShowStatus("没有可清空的缓存账号。", InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清空缓存账号",
            Content = $"将清空全部 {_sourceItems.Count} 个缓存账号并删除头像缓存，此操作不可恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _checkedKeys.Clear();
            var cleared = AppState.LoginService.ClearCachedLoginAccounts();
            Reload();
            AppState.ShowStatus($"已清空 {cleared} 个缓存账号。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus($"清空失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    // ---------- 卡片悬停 / 左上角勾选（与历史账号页一致） ----------

    private static CachedSteamLoginAccount? CardItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as CachedSteamLoginAccount;

    private void CachedCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = true;
        }
    }

    private void CachedCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = false;
        }
    }

    private void CachedCardCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        var key = account.CacheKey;
        if (account.IsSelected)
        {
            account.IsSelected = false;
            _checkedKeys.Remove(key);
        }
        else
        {
            account.IsSelected = true;
            _checkedKeys.Add(key);
        }

        UpdateBatchBar();
        UpdateControlsEnabled();
    }

    // ---------- 底部批量操作栏（勾选任意卡片后浮现，操作针对全部已勾选账号） ----------

    // 只作用于当前可见（已过滤）列表：避免搜索过滤下对看不见的勾选项执行批量删除。
    // 被过滤隐藏的勾选项仍保留在 _checkedKeys，清空搜索后会重新出现并计入。
    private List<CachedSteamLoginAccount> GetCheckedAccounts() =>
        _viewItems.Where(account => _checkedKeys.Contains(account.CacheKey)).ToList();

    private void UpdateBatchBar()
    {
        var count = GetCheckedAccounts().Count;
        BatchActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchSelectionText.Text = $"已选 {count} 项";
    }

    private void ClearCheckedSelection()
    {
        _checkedKeys.Clear();
        foreach (var account in _sourceItems)
        {
            account.IsSelected = false;
        }

        UpdateBatchBar();
        UpdateControlsEnabled();
    }

    private void BatchClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCheckedSelection();
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteAccountsWithConfirmAsync(GetCheckedAccounts());
    }

    private async Task DeleteAccountsWithConfirmAsync(IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (accounts.Count == 0)
        {
            AppState.ShowStatus("请先勾选要删除的缓存账号。", InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join("、", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? $"将删除 {nameText} 等 {accounts.Count} 个缓存账号，仅移除本地缓存记录与头像。"
            : $"将删除 {nameText}（共 {accounts.Count} 个），仅移除本地缓存记录与头像。";

        var dialog = new ContentDialog
        {
            Title = "删除缓存账号",
            Content = summary,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // 删除前把这些键移出批量选择集，避免重建后残留在已选状态。
            foreach (var account in accounts)
            {
                _checkedKeys.Remove(account.CacheKey);
            }

            var removed = AppState.LoginService.DeleteCachedLoginAccounts(accounts);
            Reload();
            AppState.ShowStatus($"已删除 {removed} 个缓存账号。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus($"删除失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private string? GetSelectedKey()
    {
        return CachedAccountList.SelectedItem is CachedSteamLoginAccount account
            ? account.CacheKey
            : null;
    }

    private void UpdateDetail()
    {
        if (CachedDetailAccountNameText is null)
        {
            return;
        }

        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            CachedDetailAvatar.ProfilePicture = null;
            CachedDetailAvatar.DisplayName = "未选择";
            CachedDetailAccountNameText.Text = "未选择账号";
            CachedDetailPersonaText.Text = "Steam 资料未同步";
            CachedDetailSteamIdText.Text = "Steam64 未记录";
            CachedDetailTimeText.Text = "暂无缓存时间";
            return;
        }

        CachedDetailAvatar.DisplayName = account.AccountTitle;
        CachedDetailAvatar.ProfilePicture = account.AvatarImage;
        CachedDetailAccountNameText.Text = account.AccountTitle;
        CachedDetailPersonaText.Text = account.PersonaDisplayName;
        CachedDetailSteamIdText.Text = account.SteamIdDisplay;
        CachedDetailTimeText.Text = $"缓存时间：{account.CachedAtText}";
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        CachedAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        CachedSearchBox.IsEnabled = !isBusy;
        RefreshCachedButton.IsEnabled = !isBusy;
        ClearCachedButton.IsEnabled = !isBusy && !_isDialogFlowActive && _sourceItems.Count > 0;
        RestoreSelectedCachedButton.IsEnabled = !isBusy && CachedAccountList.SelectedItem is not null;
        BatchClearButton.IsEnabled = !isBusy;
        BatchDeleteButton.IsEnabled = !isBusy && !_isDialogFlowActive;
    }
}
