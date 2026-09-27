using System.Reflection;
using System.Runtime.Versioning;

[assembly: AssemblyTitle("Seek")]
[assembly: AssemblyDescription("Instant file search for Windows")]
[assembly: AssemblyProduct("Seek")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

// Without this the runtime assumes a .NET 4.0 app and turns on old compatibility quirks
// (e.g. no long path support).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
