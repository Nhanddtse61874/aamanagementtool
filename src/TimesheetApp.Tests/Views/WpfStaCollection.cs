using Xunit;

namespace TimesheetApp.Tests.Views;

// WPF permits exactly one System.Windows.Application per AppDomain. Three STA render-guard test classes
// (TeamFilterLoadTests, TaskListTabRenderTests, SettingsMembershipOverlayLoadTests) each lazily create it
// via `Application.Current ?? new Application()`. xUnit runs distinct test classes as separate collections
// IN PARALLEL by default, so two can pass the null-check and both call `new Application()` — the second
// throws "Cannot create more than one System.Windows.Application instance in the same AppDomain".
// Assigning all three to this one collection serializes them (tests within a collection never run in
// parallel, and DisableParallelization keeps them off other collections' threads too), so the first call
// creates the singleton and the rest reuse Application.Current.
[CollectionDefinition("WpfSta", DisableParallelization = true)]
public sealed class WpfStaCollection { }
