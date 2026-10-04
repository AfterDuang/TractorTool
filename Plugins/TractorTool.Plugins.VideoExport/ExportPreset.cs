using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TractorTool.Plugins.VideoExport;

public sealed record ExportPresetData(Guid Id, string Name, ExportOptions Options, bool Selected = true);

public partial class ExportPreset : ObservableObject
{
    public ExportPresetData Data { get; private set; }
    public string Name => Data.Name;
    public string Summary => $"{ExportPlan.Containers.First(c => c.Value == Data.Options.Container).Name} · " +
        ExportPlan.RateControls.First(c => c.Value == Data.Options.RateControl).Name + Environment.NewLine +
        (Data.Options.Width == 0 ? "保持源尺寸" : $"{Data.Options.Width} × {Data.Options.Height}") +
        $" · {Data.Options.VideoKbps / 1000d:0.###} Mbps / {Data.Options.AudioKbps} kbps" + Environment.NewLine +
        (Data.Options.UseCover ? "替换第一帧" : "保留原始第一帧");
    private readonly VideoViewModel _owner;
    public bool CanEdit => _owner.CanEditPresets;
    public void NotifyEditing() => OnPropertyChanged(nameof(CanEdit));
    [ObservableProperty] private bool _selected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasEstimate))] private string _estimatedSize = "";
    public bool HasEstimate => EstimatedSize.Length != 0;
    [ObservableProperty] private string _status = "待导出";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    public bool HasError => Error.Length != 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOutput))] private string _outputPath = "";
    public bool HasOutput => OutputPath.Length != 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasExecutionInfo))] private string _executionInfo = "";
    public bool HasExecutionInfo => ExecutionInfo.Length != 0;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool _canCancel;
    public IRelayCommand EditCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public ExportPreset(ExportPresetData data, VideoViewModel owner)
    {
        Data = data; _selected = data.Selected; _owner = owner;
        EditCommand = new RelayCommand(() => owner.EditPreset(this));
        CopyCommand = new RelayCommand(() => owner.CopyPreset(this));
        DeleteCommand = new RelayCommand(() => owner.DeletePreset(this));
    }
    partial void OnSelectedChanged(bool value) => _owner.PresetsChanged();
    public ExportPresetData Snapshot() => Data with { Selected = Selected };
    public void Update(ExportPresetData data)
    {
        Data = data; ResetResult();
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Summary));
    }
    public void ResetResult()
    { Status = "待导出"; Error = ""; OutputPath = ""; ExecutionInfo = ""; Progress = 0; HasProgress = false; }
    public void Prepare()
    { ResetResult(); Status = "等待导出"; HasProgress = true; IsIndeterminate = false; CanCancel = true; }
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    { CanCancel = false; _owner.CancelPreset(this); }
}
