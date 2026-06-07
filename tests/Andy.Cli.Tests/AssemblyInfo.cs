// Several tests assert against the global, mutable Andy.Cli.Themes.Theme.Current
// (theme switching, prompt/feed rendering colors). Running test classes in parallel
// lets one class change the active theme while another renders, producing flaky
// color assertions. Serialize the assembly so theme state is deterministic.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
