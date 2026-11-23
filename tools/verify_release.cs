#:project ../dotnet/src/VegasReborn.Core/VegasReborn.Core.csproj
#:property TargetFramework=net10.0-windows
#:property PublishAot=false
// Verifies a signed update.json/update.sig with the .NET updater's own checks.
// Usage: dotnet run --file tools/verify_release.cs -- update.json update.sig
using VegasReborn.Core.Update;

var release = ReleaseManifest.Verify(File.ReadAllBytes(args[0]), File.ReadAllBytes(args[1]));
Console.WriteLine("verified " + release.Version);
