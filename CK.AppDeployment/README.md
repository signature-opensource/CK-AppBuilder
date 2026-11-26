# CK.AppDeployment

Builds the deployments of a CKomposable solution: the folders of its `Deployment` folder (next to its central
`ckonf.json`) whose name starts with `#` (`#Production`, `Paris/Shop/#Staging`). A deployment is an overlay of the
central configuration (see "The deployments" in [the App builder model](https://github.com/signature-opensource/CK-ResourceSpace/blob/stable/CK.Engine.Configuration/AppBuilder/README.md)).

The solution's `Deployment/GenerateAssets.cs` is two lines:

```csharp
#:package CK.AppDeployment@x.y.z
return await CK.Setup.AppDeployment.RunAsync( args );
```

CKli runs it after the build of the repository (`dotnet run GenerateAssets.cs -- {version}` in the `Deployment`
folder). The arguments are the version, then the names of the deployments to build (all of them without names).

## What it does

For each deployment and each AppContext that it deploys (`"Deployment": { "AppContexts": [...] }`, all by default):

1. **It generates a triplet** in `Deployment/.build/{AssetName}/` (ignored by git; the asset name drops the `#` and maps
   `/` to `.`: `Production`, `Paris.Shop.Staging`), with the path of the original Builder
   (`Deployment/.build/Production/Front/Front.Builder`):
   - the Builder: a copy of the files that git tracks in the original one (without its `ckonf.json`), and a
     `ckonf.deployment.json` that makes it build the deployment: it projects the merged configuration of the
     deployment in its `ckonf.json`;
   - the hosts of its Apps: a copy of the files that git tracks in the original ones (named like them), without
     `Properties`;
   - the `.App` projects are not copied: the project references of the copies are re-targeted on the originals (and on
     the copied Builder for a host);
   - for an App to which the deployment adds packages (see below), a wrapper project next to the Builder, named like the
     original `.App` folder (`Deployment/.build/Production/Front/Front.App/Front.App.Deployment.csproj`): it references the
     original `.App` project and the added packages, and the copies reference it instead of the original.

   The outputs of a previous run (`bin`, `obj`, `node_modules`, the installed packages of the client applications: their
   yarn cache and Plug'n'Play files) are kept: the builds are incremental. A failing
   deployment is a triplet that can be opened and built by hand.
2. **It publishes each host** (`dotnet publish -c Release` with the version) in `Deployment/Assets/{AssetName}-{Project}`
   (named after the projects of the App: `Production-Auth3.Front`): the build of the host runs its generated Builder. CKli zips each folder of `Assets`: one
   release asset per App (`Production-Auth3.Front.zip`). With `"SingleZipAsset": true`, the hosts are in
   `Deployment/Assets/{AssetName}/{Project}`: one asset per deployment (`Production.zip`). The MSBuild properties of the publication (the `"Publish"` of the deployment's settings and of its App:
   `RuntimeIdentifier`, `SelfContained`, `PublishSingleFile`... applied as-is) are written in a `<PropertyGroup>` of the
   copied host project, not on the command line: a global property would reach the generated Builder, that runs on the
   build machine during the build.
3. **It adds the manifest** that the Builder wrote (`$StObjGen/Deployment.manifest`), with the version, to the
   `CK.AppHost.Published.json` of the published host (its `"Deployment"`): the full name of the party, the deployment and
   its bases, the publication properties, the names of the secrets that the host needs (given at deploy time: the
   `Secrets` section of the host's configuration), the values per deployment (its addresses, its public URLs), the
   instances of a fleet.

## The packages that a deployment adds to an App

A deployment can add packages to an App: the App of the solution doesn't reference them, the deployed one does (a
desktop packaging of a web application, for instance). The deployment names them, the script gives their versions:

```jsonc
// Deployment/#Production/ckonf.json
"Deployment": { "Apps": { "$Front": { "Packages": [ "Some.Extension" ] } } }
```

```csharp
// Deployment/GenerateAssets.cs
#:package CK.AppDeployment@x.y.z
//ck-package Some.Extension@x.y.z
return await CK.Setup.AppDeployment.RunAsync( args );
```

- **The version is the one of the World:** a `//ck-package Name@Version` line is a comment for `dotnet` (nothing is
  restored for the script), and a package reference of the solution for CKli (it orders the builds, is upgraded to the
  World's version, and a new version of the package triggers a build of the solution). An added package without its line
  is an error, so is a line without version.
- **The wrapper project** references the original `.App` project and the added packages and copies them to its output:
  the generated Builder uses it as the `.App` project of the App (`CentralDeployment.GetAppProjectFolder`), so the
  BinPath of the App has both, and the host references it. Its own assembly has no code: the engine ignores it.
- **What a package does is its own business:** its types and its aspects (the Builder applies them), its MSBuild files
  (`buildTransitive`: they reach the host through the wrapper). CK.AppDeployment only adds it.
- **The App must discover its folder:** an App whose type set is restricted (`"Assemblies"` or `"Types"`) would ignore
  the added packages, so it is an error.

## What the build of a deployment never does

The build of a deployment never writes in the `#Dev` folder, never reads a secret and never sets up a database: an
AppContext whose engine sets up a database at build time (the SqlServer aspect) fails, it must be left out of the
deployment. The client applications of a host are built by its publication (CK.TS.AspNet.Clients) and published with
it; a published host uses its own folder as its content root (CK.AppHost).
