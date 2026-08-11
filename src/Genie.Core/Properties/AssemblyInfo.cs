using System.Runtime.CompilerServices;

// Exposes internal types (e.g. ScriptExpression) to the unit-test assembly so
// tests can drive the script-language evaluator directly. Test-only; no effect
// on shipped behavior.
[assembly: InternalsVisibleTo("Genie.Core.Tests")]

// Exposes GenieCore.PublishGameEventForTests to Genie.App's test assembly so
// StreamTabsViewModel tests can drive the real GameEvents relay without
// reflecting into a private field. Test-only; no effect on shipped behavior.
[assembly: InternalsVisibleTo("Genie.App.Tests")]
