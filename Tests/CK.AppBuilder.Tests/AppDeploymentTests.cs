using CK.Core;
using CK.Setup;
using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using static CK.Testing.MonitorTestHelper;

namespace CK.AppBuilder.Tests;

[TestFixture]
public class AppDeploymentTests
{
    static NormalizedPath NewFolder()
    {
        NormalizedPath root = Path.Combine( Path.GetTempPath(), "CK.AppBuilder.Tests", Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( root );
        return root;
    }

    [Test]
    public void the_versions_of_the_added_packages_are_the_ck_package_lines_of_the_script()
    {
        var script = NewFolder().AppendPart( AppDeployment.ScriptFileName );
        File.WriteAllText( script, """
            #:package CK.AppDeployment@37.0.1-0.uc.1.ci.20
            //ck-package CK.Ng.AppHost.DesktopNgApp@1.0.0-0.uc.0.ci.3
              //ck-package Other.Package@2.0.0
            // ck-package Not.A.Directive@1.0.0
            return await CK.Setup.AppDeployment.RunAsync( args );
            """.Replace( "\n", "\r\n" ) );
        var versions = AppDeployment.ReadPackageVersions( TestHelper.Monitor, script ).ShouldNotBeNull();
        versions.OrderBy( kv => kv.Key ).Select( kv => $"{kv.Key}@{kv.Value}" )
                .ShouldBe( ["CK.Ng.AppHost.DesktopNgApp@1.0.0-0.uc.0.ci.3", "Other.Package@2.0.0"],
                           "#:package is the script's own reference; the CRLF is not part of the version." );

        AppDeployment.ReadPackageVersions( TestHelper.Monitor, script.RemoveLastPart().AppendPart( "None.cs" ) )
                     .ShouldNotBeNull().ShouldBeEmpty( "No script: no version." );
    }

    [TestCase( "//ck-package Some.Package", "a version is required" )]
    [TestCase( "//ck-package Some.Package@", "a version is required" )]
    [TestCase( "//ck-package Some.Package@1.0.0\n//ck-package Some.Package@2.0.0", "has two versions of 'Some.Package'" )]
    public void invalid_ck_package_lines( string text, string message )
    {
        var script = NewFolder().AppendPart( AppDeployment.ScriptFileName );
        File.WriteAllText( script, text );
        using( TestHelper.Monitor.CollectTexts( out var texts ) )
        {
            AppDeployment.ReadPackageVersions( TestHelper.Monitor, script ).ShouldBeNull();
            texts.ShouldContain( t => t.Contains( message ) );
        }
    }

    // The original ".App" project of the App "$Front": "Front/Sample.Front.App/Sample.Front.App.csproj".
    static NormalizedPath AppProject( CentralConfiguration c )
    {
        var folder = c.AllApps.Single().AppProjectFolder;
        return folder.AppendPart( folder.LastPart + ".csproj" );
    }

    // A solution with one AppContext "Front" (its App "$Front" in "Front/Sample.Front.App"), and a "#Production" deployment.
    static (CentralConfiguration Central, CentralDeployment Production) CreateSolution( string production, string app = "{}" )
    {
        var root = NewFolder();
        var central = $$"""
            { "Domain": "Sample", "AppContexts": { "Front": { "RootPath": "Front", "App": {{app}} } } }
            """;
        File.WriteAllText( root.AppendPart( CentralConfiguration.FileName ), central );
        var deployment = root.AppendPart( CentralDeployment.FolderName ).AppendPart( "#Production" );
        Directory.CreateDirectory( deployment );
        File.WriteAllText( deployment.AppendPart( CentralConfiguration.FileName ), production );
        var c = CentralConfiguration.Parse( TestHelper.Monitor, root.AppendPart( CentralConfiguration.FileName ), central ).ShouldNotBeNull();
        var appFolder = c.AllApps.Single().AppProjectFolder;
        Directory.CreateDirectory( appFolder );
        File.WriteAllText( AppProject( c ), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """ );
        CentralDeployment.TryLoad( TestHelper.Monitor, c, out var deployments ).ShouldBeTrue();
        return (c, deployments.Single());
    }

    [Test]
    public void the_wrapper_project_references_the_original_App_and_the_added_packages()
    {
        var (central, production) = CreateSolution( """{ "Deployment": { "Apps": { "$Front": { "Packages": [ "Some.Extension" ] } } } }""" );
        var app = production.Configuration.AllApps.Single();
        var original = central.AllApps.Single();
        var builderFolder = central.RootFolder.Combine( "Deployment/.build/Production/Front/Front.Builder" );
        var folder = production.GetAppProjectFolder( builderFolder, original );
        // A previous wrapper of another name is removed.
        Directory.CreateDirectory( folder );
        File.WriteAllText( folder.AppendPart( "Old.csproj" ), "<Project />" );

        var project = AppDeployment.WriteWrapperProject( TestHelper.Monitor,
                                                         production,
                                                         app,
                                                         AppProject( central ),
                                                         folder,
                                                         production.Settings.GetPackages( app.Name ),
                                                         new System.Collections.Generic.Dictionary<string, string> { ["Some.Extension"] = "1.2.3" } )
                                  .ShouldNotBeNull();
        project.ShouldBe( builderFolder.RemoveLastPart().Combine( "Sample.Front.App/Sample.Front.App.Deployment.csproj" ),
                          "Named like the original folder: the BinPath of the App keeps its name." );
        Directory.GetFiles( folder ).Select( Path.GetFileName ).ShouldBe( ["Sample.Front.App.Deployment.csproj"] );
        var text = File.ReadAllText( project );
        text.ShouldStartWith( "<!-- Written by CK.AppDeployment: the App '$Front' and the packages that the deployment '#Production' adds to it. -->" );
        text.ShouldContain( "<TargetFramework>net10.0</TargetFramework>" );
        text.ShouldContain( "<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>" );
        text.ShouldContain( $"""<ProjectReference Include="{Path.Combine( "..", "..", "..", "..", "..", "Front", "Sample.Front.App", "Sample.Front.App.csproj" )}" />""" );
        text.ShouldContain( """<PackageReference Include="Some.Extension" Version="1.2.3" />""" );
    }

    // An "obj/project.assets.json" whose package "CK.StObj.Model" ([assembly: RequiredEngine( "CK.StObj.Runtime" )]) is the
    // assembly of the test's output, with or without the engine in the closure.
    static NormalizedPath WriteAssets( bool withEngine )
    {
        var output = AppContext.BaseDirectory.Replace( '\\', '/' );
        var engine = withEngine ? """, "CK.StObj.Runtime/1.0.0": { "type": "package" }""" : "";
        var engineLibrary = withEngine ? """, "CK.StObj.Runtime/1.0.0": { "type": "package", "path": "none" }""" : "";
        var assets = NewFolder().AppendPart( "project.assets.json" );
        File.WriteAllText( assets, $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "CK.StObj.Model/1.0.0": { "type": "package", "runtime": { "CK.StObj.Model.dll": {} } },
                  "Some.App/1.0.0": { "type": "project" }{{engine}}
                },
                "net10.0/win-x64": {}
              },
              "libraries": {
                "CK.StObj.Model/1.0.0": { "type": "package", "path": "." },
                "Some.App/1.0.0": { "type": "project", "path": "../Some.App/Some.App.csproj" }{{engineLibrary}}
              },
              "packageFolders": { "{{output}}": {} }
            }
            """ );
        return assets;
    }

    [Test]
    public void the_engines_that_the_closure_requires_and_doesnt_have_are_found()
    {
        var missing = AppDeployment.FindMissingEngines( TestHelper.Monitor, WriteAssets( withEngine: false ) ).ShouldNotBeNull();
        missing.Keys.ShouldBe( ["CK.StObj.Runtime"] );
        missing["CK.StObj.Runtime"].ShouldBe( ["CK.StObj.Model"] );

        AppDeployment.FindMissingEngines( TestHelper.Monitor, WriteAssets( withEngine: true ) ).ShouldNotBeNull()
                     .ShouldBeEmpty( "The engine is in the closure." );
    }

    [Test]
    public void the_missing_engines_are_added_to_the_generated_Builder_at_the_version_of_their_ck_package_line()
    {
        var (_, production) = CreateSolution( """{ "Deployment": { "Apps": { "$Front": { "Packages": [ "Some.Extension" ] } } } }""" );
        var builder = NewFolder().AppendPart( "Front.Builder.csproj" );
        File.WriteAllText( builder, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="CK.AppBuilder" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """ );
        var missing = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>>
        {
            ["Some.Engine"] = ["Some.Model"]
        };
        using( TestHelper.Monitor.CollectTexts( out var texts ) )
        {
            AppDeployment.AddEngineReferences( TestHelper.Monitor, production, builder, missing, new System.Collections.Generic.Dictionary<string, string>(), out _ )
                         .ShouldBeFalse();
            texts.ShouldContain( t => t.Contains( "needs the engine 'Some.Engine' (required by 'Some.Model'), but the script 'GenerateAssets.cs' has no '//ck-package Some.Engine@<version>' line" ) );
        }
        AppDeployment.AddEngineReferences( TestHelper.Monitor, production, builder, missing, new System.Collections.Generic.Dictionary<string, string> { ["Some.Engine"] = "2.0.0" }, out var added )
                     .ShouldBeTrue();
        added.ShouldBe( [("Some.Engine", "2.0.0")] );
        var text = File.ReadAllText( builder );
        text.ShouldContain( """<PackageReference Include="Some.Engine" Version="2.0.0" />""" );
        text.ShouldContain( "The engines that the packages of the deployment '#Production' require" );
        text.TrimEnd().ShouldEndWith( "</Project>" );
    }

    [Test]
    public void the_published_architecture_ends_with_the_publication()
    {
        var text = AppDeployment.DescribePublication( "1.0.0", "1.0.0/abc/2026-11-27",
                                                      [("CK.Ng.AppHost.DesktopNgApp", "0.0.1")],
                                                      [] ).ReplaceLineEndings( "\n" );
        text.ShouldBe( """
            ## The publication

            - Version: `1.0.0`
            - Build: `1.0.0/abc/2026-11-27`
            - Packages added by the deployment: `CK.Ng.AppHost.DesktopNgApp@0.0.1`
            - Engines added to the Builder: *(none)*

            """ );
    }

    [TestCase( "{}", "1.2.3", "has no '//ck-package Some.Extension@<version>' line" )]
    [TestCase( """{ "Types": [ "A.B, C" ] }""", "1.2.3", "the App has \"Types\"" )]
    [TestCase( """{ "Assemblies": [ "C" ] }""", "1.2.3", "the App has \"Assemblies\"" )]
    public void the_wrapper_project_needs_the_versions_and_a_discovered_App( string app, string? version, string message )
    {
        var (central, production) = CreateSolution( """{ "Deployment": { "Apps": { "$Front": { "Packages": [ "Some.Extension" ] } } } }""", app );
        var versions = new System.Collections.Generic.Dictionary<string, string>();
        if( app != "{}" && version != null ) versions["Some.Extension"] = version;
        var deployed = production.Configuration.AllApps.Single();
        var original = central.AllApps.Single();
        using( TestHelper.Monitor.CollectTexts( out var texts ) )
        {
            AppDeployment.WriteWrapperProject( TestHelper.Monitor,
                                               production,
                                               deployed,
                                               AppProject( central ),
                                               central.RootFolder.Combine( "Deployment/.build/Production/Front/Sample.Front.App" ),
                                               production.Settings.GetPackages( deployed.Name ),
                                               versions )
                         .ShouldBeNull();
            texts.ShouldContain( t => t.Contains( message ) );
        }
    }
}
