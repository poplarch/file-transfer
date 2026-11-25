using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FileTransfer.App.ViewModels;

namespace FileTransfer.App.Views;

/// <summary>
/// Builds and presents theme-aware modal dialogs owned by MainWindow.
/// </summary>
internal static class WindowDialogHelper
{
    public static async Task ShowSettingsDialogAsync(Window owner, MainWindowViewModel vm)
    {
        var dialog = new Window
        {
            Title = "设置",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var themeOptions = new[]
        {
            new KeyValuePair<string, string>("System", "跟随系统 (System)"),
            new KeyValuePair<string, string>("Light", "浅色模式 (Light)"),
            new KeyValuePair<string, string>("Dark", "深色模式 (Dark)")
        };

        var themeCombo = new ComboBox
        {
            ItemsSource = themeOptions,
            DisplayMemberBinding = new Avalonia.Data.Binding("Value"),
            SelectedValueBinding = new Avalonia.Data.Binding("Key"),
            SelectedValue = vm.ThemeMode,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        themeCombo.SelectionChanged += (_, _) =>
        {
            if (themeCombo.SelectedValue is string mode)
            {
                vm.ThemeMode = mode;
            }
        };

        var vimCheck = new CheckBox
        {
            Content = "启用 Vim 键盘模式（j/k 上下，h/l 上级/进入，g/G 首尾）",
            IsChecked = vm.VimModeEnabled
        };
        vimCheck.IsCheckedChanged += (_, _) => vm.VimModeEnabled = vimCheck.IsChecked == true;

        var rememberCheck = new CheckBox
        {
            Content = "记住连接密码（本机加密存储，取消后断开即清空）",
            IsChecked = vm.RememberPasswords
        };
        rememberCheck.IsCheckedChanged += (_, _) =>
        {
            vm.RememberPasswords = rememberCheck.IsChecked == true;
            vm.SaveCurrentInputs();
        };

        var helpBtn = CreateSecondaryButton("快捷键与操作说明 (F1)", 150);
        helpBtn.HorizontalAlignment = HorizontalAlignment.Left;
        helpBtn.Click += async (_, _) => await ShowHelpDialogAsync(dialog);

        var closeBtn = CreatePrimaryButton("完成", 90);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Escape or Key.Enter)
            {
                dialog.Close();
            }
            else if (args.Key == Key.F1)
            {
                args.Handled = true;
                _ = ShowHelpDialogAsync(dialog);
            }
        };

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(helpBtn, 0);
        Grid.SetColumn(closeBtn, 2);
        footer.Children.Add(helpBtn);
        footer.Children.Add(closeBtn);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "综合设置", FontSize = 15, FontWeight = FontWeight.SemiBold },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "外观主题", FontSize = 12.5, FontWeight = FontWeight.SemiBold },
                        themeCombo
                    }
                },
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "操作与安全", FontSize = 12.5, FontWeight = FontWeight.SemiBold },
                        vimCheck,
                        rememberCheck,
                        new TextBlock
                        {
                            Text = "提示：关闭 Vim 模式时，在列表直接敲击字母/数字即可按 Explorer++ 风格首字母及拼音循环定位文件；按 / 可快速聚焦过滤框，Ctrl + , 可随时打开本设置。",
                            FontSize = 11.5,
                            Opacity = 0.75,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                footer
            }
        };

        await dialog.ShowDialog(owner);
    }

    public static async Task ShowHelpDialogAsync(Window owner)
    {
        var dialog = new Window
        {
            Title = "快捷键与操作说明",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var sections = new (string SectionTitle, (string Key, string Desc)[] Items)[]
        {
            ("1. 焦点切换与目录导航", new[]
            {
                ("Tab / Alt+1 / Alt+2", "在左侧本地与右侧远端列表之间切换焦点"),
                ("Enter", "进入目录；打开本地文件；单选/批量确认下载远端文件"),
                ("Backspace / Alt+↑", "返回当前列表的上级目录"),
                ("Ctrl + L", "聚焦路径输入框（Enter 跳转，Esc 退回列表）"),
                ("F5", "刷新左右两侧文件列表")
            }),
            ("2. 过滤搜索与快速定位", new[]
            {
                ("A-Z / 0-9", "非 Vim 模式下直接敲键，按首字母/拼音循环定位文件"),
                ("/ 或 Ctrl + F", "聚焦当前面板过滤框（支持拼音；Enter 进列表，Esc 清空）"),
                ("j / k / h / l / g / G", "Vim 模式（设置中开启）：上下移动、上级/进入、首尾行")
            }),
            ("3. 文件传输与管理", new[]
            {
                ("Ctrl + U / Ctrl + D", "上传左侧选中项 / 下载右侧选中项到左侧"),
                ("Ctrl + Shift + N / F7", "在当前焦点面板新建文件夹/目录"),
                ("F2", "重命名当前选中的本地或远端文件/目录"),
                ("Ctrl + Shift + C", "复制选中文件或当前目录路径"),
                ("Delete", "删除选中项（本地移至回收站；远端拦截系统高危目录）")
            }),
            ("4. 设置与帮助", new[]
            {
                ("Ctrl + ,", "打开综合设置（System/Light/Dark 主题、Vim 模式、记住密码）"),
                ("F1", "打开本快捷键与操作说明")
            })
        };

        var sectionsPanel = new StackPanel { Spacing = 12 };
        foreach (var (sectionTitle, items) in sections)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("165,*") };
            for (var i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                var keyBadge = new Border
                {
                    BorderBrush = Brush.Parse("#8B949E"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2),
                    Margin = new Thickness(0, 0, 8, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = items[i].Key,
                        FontSize = 11.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush.Parse("#0969DA")
                    }
                };
                Grid.SetRow(keyBadge, i);
                Grid.SetColumn(keyBadge, 0);

                var desc = new TextBlock
                {
                    Text = items[i].Desc,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 0, 0, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(desc, i);
                Grid.SetColumn(desc, 1);

                grid.Children.Add(keyBadge);
                grid.Children.Add(desc);
            }

            sectionsPanel.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = sectionTitle,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush.Parse("#0969DA")
                    },
                    grid
                }
            });
        }

        var closeBtn = CreatePrimaryButton("知道了", 90);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Escape or Key.Enter)
            {
                dialog.Close();
            }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "快捷键与操作说明", FontSize = 15.5, FontWeight = FontWeight.SemiBold },
                new ScrollViewer
                {
                    MaxHeight = 520,
                    Content = sectionsPanel
                },
                new Border
                {
                    BorderBrush = Brush.Parse("#8B949E"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Child = new TextBlock
                    {
                        Text = "鼠标提示：支持 Ctrl/Shift 多选与跨栏/资源管理器拖拽互传；双击远端文件弹窗确认下载；双击列表空白处返回上级；右键菜单支持重命名与复制路径。",
                        FontSize = 12,
                        Opacity = 0.85,
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                closeBtn
            }
        };

        await dialog.ShowDialog(owner);
    }

    public static async Task<string?> ShowPromptInputDialogAsync(Window owner, string title, string label, string initialText = "")
    {
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var inputBox = new TextBox
        {
            Text = initialText,
            Watermark = "请输入名称",
            FontSize = 13.5
        };

        var okBtn = CreatePrimaryButton("确定", 88);
        var cancelBtn = CreateSecondaryButton("取消", 80);

        void Submit()
        {
            result = inputBox.Text;
            dialog.Close();
        }

        okBtn.Click += (_, _) => Submit();
        cancelBtn.Click += (_, _) => { result = null; dialog.Close(); };
        inputBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Submit();
            }
            else if (args.Key == Key.Escape)
            {
                args.Handled = true;
                result = null;
                dialog.Close();
            }
        };

        dialog.Opened += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = label, FontSize = 13 },
                inputBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Thickness(0, 4, 0, 0),
                    Children = { cancelBtn, okBtn }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<OverwriteDecision> ShowOverwriteDialogAsync(Window owner, string title, string message)
    {
        var result = OverwriteDecision.Cancel;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var overwriteBtn = CreatePrimaryButton("覆盖", 84);
        var skipBtn = CreateSecondaryButton("跳过同名", 90);
        var cancelBtn = CreateSecondaryButton("取消", 76);

        overwriteBtn.Click += (_, _) => { result = OverwriteDecision.OverwriteAll; dialog.Close(); };
        skipBtn.Click += (_, _) => { result = OverwriteDecision.SkipExisting; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = OverwriteDecision.Cancel; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13.5,
                    LineHeight = 20
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancelBtn, skipBtn, overwriteBtn }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ShowConfirmDialogAsync(Window owner, string title, string message, string confirmText, bool isDanger)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var confirmBtn = CreatePrimaryButton(confirmText, 96, isDanger);
        var cancelBtn = CreateSecondaryButton("取消", 80);

        confirmBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = false; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13.5,
                    LineHeight = 20
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Thickness(0, 6, 0, 0),
                    Children = { cancelBtn, confirmBtn }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    private static Button CreatePrimaryButton(string text, double minWidth, bool isDanger = false) => new()
    {
        Content = text,
        MinWidth = minWidth,
        Height = 32,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = Brush.Parse(isDanger ? "#CF222E" : "#0969DA"),
        Foreground = Brushes.White,
        FontWeight = FontWeight.SemiBold
    };

    private static Button CreateSecondaryButton(string text, double minWidth) => new()
    {
        Content = text,
        MinWidth = minWidth,
        Height = 32,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
}
