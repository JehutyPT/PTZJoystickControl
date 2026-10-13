using System.Runtime.CompilerServices;

// The test project's assembly is named "Tests" (no AssemblyName in Tests.csproj).
[assembly: InternalsVisibleTo("Tests")]
// SkaarhojPanel saves its settings through AtomicFile.
[assembly: InternalsVisibleTo("SkaarhojPanel")]
