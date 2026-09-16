using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DbaToolsStudio.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;

namespace DbaToolsStudio;

public class CategoryItem
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public partial class MainWindow : Window
{
    private readonly PowerShellService _psService;
    private List<DbaCommandMetadata> _allCommands = new();
    private List<DbaCommandMetadata> _filteredCommands = new();
    private DbaCommandMetadata? _currentCommand;
    private List<DbaCommandParameterInfo> _currentParameters = new();
    private List<IDictionary<string, object>> _lastResults = new();
    private string _currentCategory = "All Commands";
    private DispatcherTimer _searchTimer;

    public MainWindow()
    {
        InitializeComponent();
        _psService = new PowerShellService();
        
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (s, e) =>
        {
            _searchTimer.Stop();
            FilterCommands();
        };
        
        Loaded += MainWindow_Loaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Environment.Exit(0);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        SetStatus("Fetching dbatools commands and metadata...", true);
        try
        {
            _allCommands = await _psService.GetDbaCommandMetadataAsync();
            BuildCategories();
            FilterCommands();
            SetStatus($"Loaded {_allCommands.Count} commands.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading commands: {ex.Message}", false, true);
        }
    }

    private void BuildCategories()
    {
        var categoryGroups = _allCommands.GroupBy(c => c.Category).OrderBy(g => g.Key);
        
        var catList = new List<CategoryItem>
        {
            new CategoryItem { Name = "All Commands", Count = _allCommands.Count }
        };

        foreach (var group in categoryGroups)
        {
            catList.Add(new CategoryItem { Name = group.Key, Count = group.Count() });
        }

        CategoriesListBox.ItemsSource = catList;
        CategoriesListBox.SelectedIndex = 0;
    }

    private void CategoriesListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoriesListBox.SelectedItem is CategoryItem cat)
        {
            _currentCategory = cat.Name;
            FilterCommands();
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void FilterCommands()
    {
        var searchText = SearchBox.Text?.ToLower().Trim() ?? "";
        var searchTerms = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        _filteredCommands = _allCommands.Where(c => 
        {
            if (_currentCategory != "All Commands" && c.Category != _currentCategory)
                return false;

            if (searchTerms.Length == 0) return true;

            var searchableText = (c.Name + " " + c.Description).ToLower();
            
            // Allow matching words in any order (e.g. "remove database" matches "Remove-DbaDatabase")
            return searchTerms.All(term => searchableText.Contains(term));
        }).ToList();

        CommandsItemsControl.ItemsSource = _filteredCommands;
        CommandsCountText.Text = $"{_filteredCommands.Count} commands";
    }

    private async void CommandCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is DbaCommandMetadata metadata)
        {
            _currentCommand = metadata;
            DetailTitleText.Text = metadata.Name;
            DetailDescText.Text = metadata.Description;
            DetailExamplesText.Text = "Loading examples...";
            
            CommandsViewPanel.IsVisible = false;
            DetailViewPanel.IsVisible = true;
            
            ExecuteButton.IsEnabled = false;
            ParametersPanel.Children.Clear();
            ResultsDataGrid.ItemsSource = null;
            ResultsSearchBox.IsEnabled = false;

            SetStatus($"Loading parameters for {metadata.Name}...", true);
            
            // Load examples in background
            _ = LoadExamplesAsync(metadata.Name);
            
            try
            {
                _currentParameters = await _psService.GetCommandParametersAsync(metadata.Name);
                BuildDynamicForm();
                ExecuteButton.IsEnabled = true;
                SetStatus("Ready", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Error loading parameters: {ex.Message}", false, true);
            }
        }
    }

    private async System.Threading.Tasks.Task LoadExamplesAsync(string commandName)
    {
        try
        {
            var examples = await _psService.GetCommandExamplesAsync(commandName);
            Dispatcher.UIThread.Post(() => 
            {
                if (examples.Count == 0)
                {
                    DetailExamplesText.Text = "No examples found.";
                }
                else
                {
                    DetailExamplesText.Text = string.Join("\n\n", examples);
                }
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() => DetailExamplesText.Text = "Failed to load examples.");
        }
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        DetailViewPanel.IsVisible = false;
        CommandsViewPanel.IsVisible = true;
        _currentCommand = null;
    }

    private void BuildDynamicForm()
    {
        ParametersPanel.Children.Clear();
        foreach (var param in _currentParameters)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160, *") };
            
            var label = new TextBlock 
            { 
                Text = param.Name + (param.IsMandatory ? " *" : ""), 
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = param.IsMandatory ? Brushes.DarkRed : Brushes.Black,
                FontWeight = param.IsMandatory ? FontWeight.Bold : FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap
            };
            ToolTip.SetTip(label, param.HelpMessage);
            grid.Children.Add(label);
            Grid.SetColumn(label, 0);

            Control inputControl;

            if (param.ParameterType == typeof(SwitchParameter) || param.ParameterType == typeof(bool))
            {
                var checkbox = new CheckBox { Name = "Param_" + param.Name };
                checkbox.IsCheckedChanged += (s, e) => UpdateCommandPreview();
                inputControl = checkbox;
            }
            else if (param.ParameterType == typeof(PSCredential))
            {
                var credPanel = new Grid { ColumnDefinitions = new ColumnDefinitions("*, 10, *"), Name = "ParamCred_" + param.Name };
                var userBox = new TextBox { Name = "ParamUser_" + param.Name, Watermark = "Username" };
                var passBox = new TextBox { Name = "ParamPass_" + param.Name, Watermark = "Password", PasswordChar = '*' };
                
                userBox.TextChanged += (s, e) => UpdateCommandPreview();
                
                credPanel.Children.Add(userBox);
                Grid.SetColumn(userBox, 0);
                credPanel.Children.Add(passBox);
                Grid.SetColumn(passBox, 2);
                inputControl = credPanel;
            }
            else
            {
                var textBox = new TextBox { Name = "Param_" + param.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
                textBox.TextChanged += (s, e) => UpdateCommandPreview();
                inputControl = textBox;
            }

            grid.Children.Add(inputControl);
            Grid.SetColumn(inputControl, 1);
            
            ParametersPanel.Children.Add(grid);
        }
        
        if (_currentParameters.Count == 0)
        {
            ParametersPanel.Children.Add(new TextBlock { Text = "No custom parameters for this command.", FontStyle = FontStyle.Italic, Foreground = Brushes.Gray });
        }
        UpdateCommandPreview();
    }

    private void UpdateCommandPreview()
    {
        if (_currentCommand == null) return;
        
        var cmd = _currentCommand.Name;
        foreach (var param in _currentParameters)
        {
            var control = ParametersPanel.Children.OfType<Grid>()
                            .SelectMany(p => p.Children)
                            .FirstOrDefault(c => c.Name == "Param_" + param.Name || c.Name == "ParamCred_" + param.Name);

            if (control is CheckBox cb && cb.IsChecked == true)
            {
                cmd += $" -{param.Name}";
            }
            else if (control is Grid sp && sp.Name == "ParamCred_" + param.Name)
            {
                var userBox = sp.Children.OfType<TextBox>().FirstOrDefault(c => c.Name == "ParamUser_" + param.Name);
                if (userBox != null && !string.IsNullOrWhiteSpace(userBox.Text))
                {
                    cmd += $" -{param.Name} $cred"; // Represent credential in preview
                }
            }
            else if (control is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                if (tb.Text.Contains(" "))
                    cmd += $" -{param.Name} \"{tb.Text}\"";
                else
                    cmd += $" -{param.Name} {tb.Text}";
            }
        }
        CommandPreviewText.Text = cmd;
    }

    private System.Threading.CancellationTokenSource? _cts;

    private async void ExecuteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentCommand == null) return;

        var parameters = new Dictionary<string, object>();
        foreach (var param in _currentParameters)
        {
            var control = ParametersPanel.Children.OfType<Grid>()
                            .SelectMany(p => p.Children)
                            .FirstOrDefault(c => c.Name == "Param_" + param.Name || c.Name == "ParamCred_" + param.Name);

            if (control is CheckBox cb && cb.IsChecked == true)
            {
                parameters[param.Name] = true;
            }
            else if (control is Grid sp && sp.Name == "ParamCred_" + param.Name)
            {
                var userBox = sp.Children.OfType<TextBox>().FirstOrDefault(c => c.Name == "ParamUser_" + param.Name);
                var passBox = sp.Children.OfType<TextBox>().FirstOrDefault(c => c.Name == "ParamPass_" + param.Name);
                
                if (userBox != null && passBox != null && !string.IsNullOrWhiteSpace(userBox.Text) && !string.IsNullOrEmpty(passBox.Text))
                {
                    var secureString = new System.Security.SecureString();
                    foreach (char c in passBox.Text) secureString.AppendChar(c);
                    parameters[param.Name] = new PSCredential(userBox.Text, secureString);
                }
            }
            else if (control is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                if (param.ParameterType == typeof(int) && int.TryParse(tb.Text, out int intVal))
                    parameters[param.Name] = intVal;
                else if (param.ParameterType == typeof(string[]))
                    parameters[param.Name] = tb.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                else
                    parameters[param.Name] = tb.Text;
            }
        }

        SetStatus($"Executing {_currentCommand.Name}...", true);
        ExecuteButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ResultsDataGrid.ItemsSource = null;

        _cts = new System.Threading.CancellationTokenSource();

        try
        {
            var results = await _psService.RunDbaCommandAsync(_currentCommand.Name, parameters, _cts.Token);
            _lastResults = results;

            ResultsDataGrid.Columns.Clear();
            if (results.Count > 0)
            {
                var firstRow = results[0];
                foreach (var key in firstRow.Keys)
                {
                    ResultsDataGrid.Columns.Add(new DataGridTextColumn
                    {
                        Header = key,
                        Binding = new Avalonia.Data.Binding($"[{key}]")
                    });
                }
            }

            ResultsDataGrid.ItemsSource = _lastResults;
            ResultsSearchBox.IsEnabled = true;
            ResultsSearchBox.Text = string.Empty;
            SetStatus($"Command executed successfully. ({results.Count} results)", false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Execution stopped by the user.", false, true);
        }
        catch (Exception ex)
        {
            SetStatus($"Execution error: {ex.Message}", false, true);
        }
        finally
        {
            ExecuteButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            SetStatus("Stopping command execution...", true);
            _cts.Cancel();
        }
    }

    private void ResultsSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (ResultsSearchBox.Text == null) return;
        
        var query = ResultsSearchBox.Text.ToLower();
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultsDataGrid.ItemsSource = _lastResults;
        }
        else
        {
            ResultsDataGrid.ItemsSource = _lastResults.Where(row => 
                row.Values.Any(val => val != null && val.ToString()?.ToLower().Contains(query) == true)
            ).ToList();
        }
    }

    private void SetStatus(string message, bool isWorking, bool isError = false)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = isError ? Brushes.LightCoral : Brushes.White;
            StatusBarProgress.IsVisible = isWorking;
        });
    }
}
