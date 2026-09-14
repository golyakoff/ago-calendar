using System.Runtime.CompilerServices;

// `24-17`: OwnerTenantScopeEndpointTests constructs `PlatformOwnerRequirement` and reads
// `PlatformOwnerRealmRole.RealmAccessClaimType` directly to build a stripped test host, the same
// visibility `ago-chat`'s own `Ago.Chat.Api/AssemblyInfo.cs` grants its own Integration.Tests project
// for the identical reason.
[assembly: InternalsVisibleTo("Ago.Calendar.Integration.Tests")]
