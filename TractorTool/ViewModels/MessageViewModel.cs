using CommunityToolkit.Mvvm.Input;
namespace TractorTool.ViewModels;
public sealed record MessageViewModel(string Title, string Message, IRelayCommand RetryCommand, IRelayCommand ShowLogsCommand);
