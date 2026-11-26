using CK.Core;
using CK.Monitoring;
using CK.TypeSet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CK.Setup;

/// <summary>
/// Implements <see cref="ICKomposableAppBuilder"/> and provides a single static Run that encapsulates
/// the whole process (including Logs).
/// <para>
/// This helper should be used with the following conventions:
/// <list type="number">
///     <item>The application code is in a project named "XXX.App".</item>
///     <item>
///     This helper is used from an executable project named "XXX.Builder" whose Program.cs can be as simple as:
///     <code>
///     return await CK.Setup.CKomposableAppBuilder.RunAsync();
///     </code>
///     </item>
///     <item>The application to setup is a project named "XXX.Host".</item>
/// </list>
/// If a <c>ckonf.json</c> exists in the "XXX.Builder" project, it is the configuration of the AppContext that the Builder
/// manages: its App (or Apps: "$X" is the "X.App" and "X.Host" projects) with the configuration of their hosts (their
/// "appsettings.json" are projections of it), the aspects, and what the <see cref="IAppBuilderContributor"/> of the
/// referenced engines deduce from them. An optional <c>CKSetup.xml</c> brings the aspects that are not deduced yet.
/// <para>
/// When the repository has a central configuration (a <c>ckonf.json</c> at its root: <see cref="CentralConfiguration"/>),
/// the <c>ckonf.json</c> of the Builder is first written from it: the Builder "{RootPath}/{Name}.Builder" manages the
/// AppContext "Name" of its "AppContexts".
/// </para>
/// <para>
/// Otherwise, if a <c>CKSetup.xml</c> exists in the "XXX.Builder" project, it automatically configures the <see cref="ICKomposableAppBuilder.EngineConfiguration"/>.
/// </para>
/// <para>
/// With the arguments <c>--check &lt;file&gt;</c> (<see cref="CheckArgument"/>), the run only checks the configuration: it
/// stops once the restrictions are applied (the projection, the aspects, the contributors, the locks, the secrets and the
/// values per deployment of the deployments), before the <c>configure</c> function, the projections of the hosts and the
/// engine run (no generated code, no database setup), and writes its warnings and errors in the file (see
/// <see cref="CheckArgument"/>). The configurator checks the documents that it saves with it.
/// </para>
/// </para>
/// </summary>
public sealed class CKomposableAppBuilder : ICKomposableAppBuilder
{
    readonly NormalizedPath _parentFolderPath;
    readonly IActivityMonitor _monitor;
    readonly NormalizedPath _msBuildOutputPath;
    readonly NormalizedPath _builderFolderPath;
    readonly EngineConfiguration _engineConfiguration;
    string _applicationName;

    /// <inheritdoc />
    public string ApplicationName
    {
        get => _applicationName;
        set => _applicationName = value ?? String.Empty;
    }

    /// <inheritdoc />
    public IActivityMonitor Monitor => _monitor;

    /// <inheritdoc />
    public NormalizedPath MsBuildOutputPath => _msBuildOutputPath;

    /// <inheritdoc />
    public NormalizedPath ParentFolderPath => _parentFolderPath;

    /// <inheritdoc />
    public NormalizedPath BuilderFolderPath => _builderFolderPath;

    /// <inheritdoc />
    public NormalizedPath GetAppFolderPath( string? appName = null ) => _parentFolderPath.AppendPart( appName ?? ApplicationName + ".App" );

    /// <inheritdoc />
    public NormalizedPath GetHostFolderPath( string? appName = null ) => _parentFolderPath.AppendPart( appName ?? ApplicationName + ".Host" );

    /// <inheritdoc />
    public NormalizedPath GetAppBinPath( string? appName = null ) => GetAppFolderPath( appName ).Combine( _msBuildOutputPath );

    /// <inheritdoc />
    public EngineConfiguration EngineConfiguration => _engineConfiguration;

    /// <summary>
    /// The argument of the check of the configuration: <c>--check &lt;file&gt;</c>. The file is written with the result,
    /// <c>{ "Success": true, "Entries": [ { "Level": "Warn", "Text": "..." }, ... ] }</c>: the warnings and the errors of the
    /// run ("Warn", "Error" or "Fatal", the text of an exception appended to its entry's), in their order. The run fails
    /// (<c>"Success": false</c>, a non zero exit code) when an error is logged.
    /// </summary>
    public const string CheckArgument = "--check";

    /// <summary>
    /// Shell method that handles the run of a setup from a ".App" to its ".Host" project.
    /// The configuration is provided from the CKSetup.xml.
    /// </summary>
    /// <returns>The standard main result (0 on success, non zero on error).</returns>
    public static Task<int> RunAsync()
    {
        return DoRunAsync( null );
    }

    /// <summary>
    /// Shell method that handles the run of a setup from a ".App" to its ".Host" project.
    /// <para>
    /// The <paramref name="configure"/> can alter the CKSetup.xml (if it exists).
    /// </para>
    /// </summary>
    /// <param name="configure">Configure the <see cref="EngineConfiguration"/>.</param>
    /// <returns>The standard main result (0 on success, non zero on error).</returns>
    public static Task<int> RunAsync( Action<IActivityMonitor, ICKomposableAppBuilder> configure )
    {
        Throw.CheckNotNullArgument( configure );
        return DoRunAsync( configure );
    }

    /// <summary>
    /// Shell method that handles the run of a setup from a ".App" to its ".Host" project.
    /// <para>
    /// The <paramref name="configure"/> can alter the CKSetup.xml (if it exists).
    /// </para>
    /// </summary>
    /// <param name="configure">Configure the <see cref="EngineConfiguration"/>.</param>
    /// <returns>The standard main result (0 on success, non zero on error).</returns>
    public static Task<int> RunAsync( Func<IActivityMonitor, ICKomposableAppBuilder, Task> configure )
    {
        Throw.CheckNotNullArgument( configure );
        return DoRunAsync( configure );
    }

    static async Task<int> DoRunAsync( object? configure = null )
    {
        NormalizedPath appContext = AppContext.BaseDirectory;
        var builderFolder = appContext.RemoveLastPart( 2 );
        if( builderFolder.LastPart != "bin" )
        {
            Console.WriteLine( $"Invalid OutputPath '{appContext}'. It must follow the 'bin/Configuration/TargetFramework' convention." );
            return -1;
        }
        builderFolder = builderFolder.RemoveLastPart();
        var rootLogPath = builderFolder.AppendPart( "Logs" );
        LogFile.RootLogPath = rootLogPath;
        GrandOutput.EnsureActiveDefault();
        var monitor = new ActivityMonitor();
        var checkFile = GetCheckFile();
        ActivityMonitorSimpleCollector? checkCollector = null;
        if( checkFile != null )
        {
            checkCollector = new ActivityMonitorSimpleCollector { MinimalFilter = LogLevelFilter.Warn, Capacity = 2000 };
            monitor.Output.RegisterClient( checkCollector );
            monitor.Info( $"Checking the configuration only (the result is written in '{checkFile}')." );
        }
        try
        {
            var parentPath = builderFolder.RemoveLastPart();
            var msBuildOutputPath = appContext.RemovePrefix( builderFolder );
            var applicationName = builderFolder.LastPart;
            if( applicationName.EndsWith( ".Builder" ) ) applicationName = applicationName.Substring( 0, applicationName.Length - 8 );

            EngineConfiguration? configuration;
            AppContextConfiguration? app = null;
            BinPathTypeGroup.Result? appSets = null;
            IReadOnlyList<IAppBuilderContributor>? contributors = null;
            IReadOnlyList<CentralDeployment> deployments = [];
            NormalizedPath ckSetupFile = builderFolder.AppendPart( "CKSetup.xml" );
            // The central configuration of the repository, if any, is the source of the builder's "ckonf.json".
            if( !CentralConfiguration.TryLoad( monitor, builderFolder, out var central ) ) return 1;
            // A builder generated for a deployment (CK.AppDeployment) projects the deployment, not the central configuration.
            if( !CentralDeployment.TryReadBuilderFile( monitor, builderFolder, out var deploymentName, out var deployedAppContext ) ) return 1;
            CentralDeployment? deployment = null;
            if( deploymentName != null )
            {
                Throw.DebugAssert( deployedAppContext != null );
                if( central == null )
                {
                    monitor.Error( $"The builder '{builderFolder}' of the deployment '{deploymentName}' is not in a repository with a central configuration." );
                    return 1;
                }
                contributors = AppBuilderContributors.Create( monitor, AppContext.BaseDirectory );
                if( contributors == null || !CentralDeployment.TryLoad( monitor, central, out var all ) ) return 1;
                deployment = all.FirstOrDefault( d => d.Name.Equals( deploymentName, StringComparison.OrdinalIgnoreCase ) );
                if( deployment == null )
                {
                    monitor.Error( $"The deployment '{deploymentName}' of the builder '{builderFolder}' doesn't exist: the deployments are '{all.Select( d => d.Name ).Concatenate( "', '" )}'." );
                    return 1;
                }
                using( monitor.OpenInfo( $"Building the AppContext '{deployedAppContext}' for the deployment '{deployment.Name}'." ) )
                {
                    if( !deployment.WriteBuilderProjection( monitor, central, deployedAppContext, builderFolder, contributors ) ) return 1;
                }
                // The values per deployment have been checked by the development build: the central projection is skipped.
                central = null;
            }
            if( central != null )
            {
                var centralAppContext = central.FindAppContextOfBuilder( builderFolder );
                if( centralAppContext == null )
                {
                    var relative = builderFolder.RemoveLastPart().RemovePrefix( central.RootFolder );
                    var name = applicationName;
                    monitor.Error( $"The Builder '{builderFolder}' manages no AppContext of the central configuration '{central.File}': add it to its \"AppContexts\" (\"{name}\": {{ {(relative.IsEmptyPath ? "" : $"\"RootPath\": \"{relative}\", ")}\"App\": {{ ... }} }})." );
                    return 1;
                }
                contributors = AppBuilderContributors.Create( monitor, AppContext.BaseDirectory );
                if( contributors == null || !central.WriteProjection( monitor, centralAppContext, contributors ) ) return 1;
                // The deployments: their merged configuration is written by every builder run (checked below).
                if( !CentralDeployment.TryLoad( monitor, central, out deployments ) ) return 1;
                foreach( var d in deployments )
                {
                    if( !d.WriteMergedFile( monitor, central ) ) return 1;
                }
            }
            if( File.Exists( builderFolder.AppendPart( AppContextConfiguration.FileName ) ) )
            {
                // The builder's configuration is the source: the Apps, their hosts' "appsettings.json" and the aspects.
                // An optional "CKSetup.xml" brings the aspects that are not deduced yet.
                app = AppContextConfiguration.Load( monitor,
                                                    builderFolder,
                                                    msBuildOutputPath,
                                                    File.Exists( ckSetupFile ) ? System.Xml.Linq.XElement.Load( ckSetupFile ) : null,
                                                    out configuration );
                if( app == null ) return 1;
                Throw.DebugAssert( configuration != null );
                // The type sets of the Apps: their aspects are found in them. The analysis of the assemblies is done once:
                // the engine run reuses it.
                appSets = BinPathTypeGroup.Run( monitor, configuration );
                if( appSets == null || !appSets.Success ) return 1;
                // The secrets of the development: for the engines that need a value at build time (a database setup). The
                // build of a deployment never reads a secret.
                IReadOnlyDictionary<string, string>? devSecrets = null;
                if( deployment == null && !app.TryLoadDevSecrets( monitor, out devSecrets ) ) return 1;
                var context = new AppBuilderContext( configuration, app.Apps, app.Aspects, devSecrets, deployment );
                // The configurable types of each App: the contributors find the type of a section (and its models) by its name.
                foreach( var g in appSets.Groups )
                {
                    if( g.IsUnifiedPure ) continue;
                    var types = g.ConfigurableTypes.Select( t => t.Type ).ToArray();
                    foreach( var a in app.Apps.Where( x => g.Configurations.Contains( x.BinPathConfiguration ) ) ) a.ConfigurableTypes = types;
                }
                // An aspect key that names no aspect of the Apps is an error before the contributors run.
                if( !AppContextAspects.Run( monitor, context, appSets )
                    || !app.CheckAspectsHandled( monitor )
                    || (contributors ??= AppBuilderContributors.Create( monitor, AppContext.BaseDirectory )) == null
                    || !AppBuilderContributors.Run( monitor, context, contributors )
                    // The restrictions ("$LOCK" and the packages' ones) once the contributors declared what they read.
                    || !AppBuilderLocks.Run( monitor, app, appSets, contributors, central, builderFolder, deployments, deployment ) )
                {
                    return 1;
                }
            }
            else if( File.Exists( ckSetupFile ) )
            {
                monitor.Info( "Loading EngineConfiguration from CKSetup.xml file." );
                configuration = EngineConfiguration.Load( ckSetupFile );
            }
            else
            {
                configuration = new EngineConfiguration() { BasePath = builderFolder };
            }
            // The check of the configuration stops here: the configuration is read, projected and checked.
            if( checkFile != null ) return 0;
            var b = new CKomposableAppBuilder( parentPath, monitor, msBuildOutputPath, applicationName, builderFolder, configuration );
            if( configure is Func<IActivityMonitor, ICKomposableAppBuilder, Task> a )
            {
                await a.Invoke( monitor, b ).ConfigureAwait( false );
            }
            else if( configure is Action<IActivityMonitor, ICKomposableAppBuilder> s )
            {
                s.Invoke( monitor, b );
            }
            // The hosts' "appsettings.json" are projections of the App's configuration.
            if( app != null && !app.WriteProjections( monitor ) ) return 1;
            var engineResult = await b.EngineConfiguration.RunAsync( monitor, appSets?.AssemblyResult ).ConfigureAwait( false );
            return engineResult != null && engineResult.Status != RunStatus.Failed ? 0 : 1;
        }
        catch( Exception ex )
        {
            monitor.Error( ex );
            return -2;
        }
        finally
        {
            if( checkFile != null ) WriteCheckFile( checkFile, checkCollector! );
            monitor.MonitorEnd();
            var d = GrandOutput.Default;
            if( d != null ) await d.DisposeAsync().ConfigureAwait( false );
        }
    }

    static string? GetCheckFile()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf( args, CheckArgument );
        if( i < 0 ) return null;
        if( i + 1 >= args.Length || string.IsNullOrWhiteSpace( args[i + 1] ) )
        {
            throw new ArgumentException( $"The argument {CheckArgument} is followed by the path of the result file." );
        }
        return Path.GetFullPath( args[i + 1] );
    }

    static void WriteCheckFile( string file, ActivityMonitorSimpleCollector collector )
    {
        var entries = collector.Entries;
        var success = !entries.Any( e => e.MaskedLevel >= LogLevel.Error );
        using var stream = File.Create( file );
        using var w = new System.Text.Json.Utf8JsonWriter( stream, new System.Text.Json.JsonWriterOptions { Indented = true } );
        w.WriteStartObject();
        w.WriteBoolean( "Success", success );
        w.WriteStartArray( "Entries" );
        foreach( var e in entries )
        {
            w.WriteStartObject();
            w.WriteString( "Level", e.MaskedLevel.ToString() );
            w.WriteString( "Text", e.Exception != null ? $"{e.Text} {e.Exception.Message}" : e.Text );
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    CKomposableAppBuilder( NormalizedPath parentPath,
                           IActivityMonitor monitor,
                           NormalizedPath msBuildOutputPath,
                           string applicationName,
                           NormalizedPath builderFolderPath,
                           EngineConfiguration configuration )
    {
        _parentFolderPath = parentPath;
        _monitor = monitor;
        _msBuildOutputPath = msBuildOutputPath;
        _applicationName = applicationName;
        _builderFolderPath = builderFolderPath;
        if( configuration.FirstBinPath.Path.IsEmptyPath )
        {
            configuration.FirstBinPath.Path = GetAppBinPath();
        }
        if( configuration.FirstBinPath.ProjectPath.IsEmptyPath )
        {
            configuration.FirstBinPath.ProjectPath = GetHostFolderPath();
        }
        _engineConfiguration = configuration;
    }
}
