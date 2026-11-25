# CK.AppBuilder

Runs the CKomposable engine from a "XXX.Builder" project, whose `Program.cs` is one line:

```csharp
return await CK.Setup.CKomposableAppBuilder.RunAsync();
```

The builder reads its `ckonf.json` (its Apps and their aspects), computes the type sets of its Apps, applies the
aspects found in them, runs the contributors of the engines it references, then the engine (that reuses the analysis
of the assemblies), and writes the `appsettings.json` of the hosts: the `ckonf.json`, its projections and the
contributors' contract are described in [the App builder model](https://github.com/signature-opensource/CK-ResourceSpace/blob/stable/CK.Engine.Configuration/AppBuilder/README.md)
(CK.Engine.Configuration). Without `ckonf.json`, the `CKSetup.xml` of the builder configures the engine.

When the repository has a central configuration (a `ckonf.json` at its git root), the builder first writes its own
`ckonf.json` from its entry in it (with the contributors' `IAppBuilderContributor.ProjectFromCentral`): a builder that is
not in its `"Builders"` is an error.

A builder generated for a deployment (by [CK.AppDeployment](../CK.AppDeployment/README.md): its folder has a
`ckonf.deployment.json`) writes its `ckonf.json` from the merged configuration of the deployment instead, never reads a
secret (`AppBuilderContext.Deployment` is set), and writes the `Deployment.json` manifest of each of its hosts.

**The check of the configuration:** `dotnet run --property:CKAppBuilderRunAfterBuild=false --project X.Builder -- --check <file>` (`CKomposableAppBuilder.CheckArgument`; see below for the property)
runs the builder up to the restrictions (the projection, the aspects, the contributors, the locks, the secrets and the
values per deployment of the deployments) and stops before the engine: no generated code, no database setup, no
`appsettings.json` of the hosts (the builder's own `ckonf.json` and the merged files of the deployments are written, as
by every run). Its warnings and errors are written in the file, `{ "Success": false, "Entries": [ { "Level": "Error",
"Text": "..." } ] }`, and an error is a non zero exit code. The configurator checks the documents it saves with it.

A builder runs after its build (the `RunMeAfterBuild` target of `CK.AppBuilder.targets`): `-p:CKAppBuilderRunAfterBuild=false`
builds it without running it (the check builds it this way, then runs it with `--check`).

## The contributors

The builder finds the contributors (`[assembly: AppBuilderContributor( typeof( MyContributor ) )]`) in its own folder:
nothing to write in the "XXX.Builder" project. It runs them before the engine.

- **A contributor is ordered like any resource group:** its `[Requires]` and `[RequiredBy]` (and their `[Optional...]`
  variants: an optional one is ignored when its target is not a contributor of the builder) are read by the reader of
  the ResourceSpace (`ResPackageRelationReader`). `[Children]`, `[Groups]` and `[Package]` are errors, and so is a
  cycle.
- **A constructor parameter is a requirement:** a contributor has one public constructor whose parameters are other
  contributors (an implicit `[Requires]`); they are created and run before it.

## The engines

The engines that the models of the Apps require (their `[RequiredEngine]`, and the ones these engines require) must be
loadable from the builder's folder: the builder references them. Before the run, the engine checks it and one error
lists the missing ones with the models that require them (`CKEngine.CheckRequiredEngines`).
