// include Fake libs

// #r "paket:
//     source https://nuget.org/api/v2
//     framework: net46
//     nuget FSharp.Core  redirects:force, content:none
//     nuget Fake.Core >= 5.0.0
//     nuget Fake.Core.ReleaseNotes
//     nuget Fake.IO.FileSystem
//     nuget Fake.Tools.Git
//     nuget Fake.DotNet.Cli
//     nuget Fake.DotNet.MSBuild
//     nuget FSharp.Formatting
//     github fsharp/FAKE modules/Octokit/Octokit.fsx
//     //"
#r "./packages/build/FAKE/tools/FakeLib.dll"
open Fake.Core
open Fake.Runtime
#r "System.IO.Compression.FileSystem"
// #load "./.fake/build.fsx/intellisense.fsx"

open System
open System.IO
open Fake
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
// open Fake.NpmHelper
open Fake.Tools.Git


// Filesets
let projects  =
    !! "src/**.fsproj"
    ++ "netstandard/**.fsproj"


// let dotnetcliVersion = DotNet.getSDKVersionFromGlobalJson()

let mutable dotnetExePath = "dotnet"

let baseOptions = lazy DotNet.install DotNet.Release_2_1_300
let withWorkDir workingDir =
    DotNet.Options.lift baseOptions.Value
    >> DotNet.Options.withWorkingDirectory workingDir
    >> DotNet.Options.withDotNetCliPath dotnetExePath
    // DotNetCli.RunCommand (fun p -> { p with ToolPath = dotnetExePath
    //                                       WorkingDir = workingDir } )

// Fake.Core.Target.create "InstallDotNetCore" (fun _ ->
//    dotnetExePath <- DotNetCli.InstallDotNetSDK dotnetcliVersion
// )

Core.Target.create "Clean" (fun _ ->
    IO.Shell.cleanDir "src/obj"
    IO.Shell.cleanDir "src/bin"
    IO.Shell.cleanDir "netstandard/obj"
    IO.Shell.cleanDir "netstandard/bin"
)

Core.Target.create "Install" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = IO.Path.GetDirectoryName s
        DotNet.restore (fun a -> {a with Common = a.Common |> withWorkDir dir}) s
        // runDotnet "restore"  dir
    )
)

Core.Target.create "Build" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = IO.Path.GetDirectoryName s
        DotNet.build (fun a ->
            let c =
                { a.Common with
                    CustomParams = (Some "-c Release /p:SourceLinkCreate=true")
                    WorkingDirectory = dir }
            { a with Common = c }) s
    // runDotnet dir "build -c Release /p:SourceLinkCreate=true"
    )
)

let release = ReleaseNotes.load "RELEASE_NOTES.md"

Core.Target.create "Meta" (fun _ ->
    [ "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
      "<PropertyGroup>"
      "<PackageProjectUrl>https://github.com/elmish/elmish</PackageProjectUrl>"
      "<PackageLicenseUrl>https://raw.githubusercontent.com/elmish/elmish/master/LICENSE.md</PackageLicenseUrl>"
      "<PackageIconUrl>https://raw.githubusercontent.com/elmish/elmish/master/docs/files/img/logo.png</PackageIconUrl>"
      "<RepositoryUrl>https://github.com/elmish/elmish.git</RepositoryUrl>"
      sprintf "<PackageReleaseNotes>%s</PackageReleaseNotes>" (List.head release.Notes)
      "<PackageTags>fable;elm;fsharp</PackageTags>"
      "<Authors>Eugene Tolmachev</Authors>"
      sprintf "<Version>%s</Version>" (string release.SemVer)
      "</PropertyGroup>"
      "</Project>"]
    |> IO.File.write false "Directory.Build.props"
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

Core.Target.create "Package" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = IO.Path.GetDirectoryName s
        DotNet.pack (fun a ->
            let c =
                { a.Common with
                    CustomParams = Some "-c Release"
                    WorkingDirectory = dir }
            { a with Common = c }
        ) s
    )
)

Core.Target.create "PublishNuget" (fun _ ->
    let args = sprintf "nuget push Fable.Elmish.%s.nupkg -s nuget.org -k %s" (string release.SemVer) (Environment.environVar "nugetkey")
    // DotNet.exec (fun a -> { a with  })
    let result = DotNet.exec (fun a -> a |> withWorkDir "src/bin/Release") "run" args
    if (not result.OK) then failwith (List.reduce (+) result.Errors)
    // runDotnet "src/bin/Release" args
    let args = sprintf "nuget push Elmish.%s.nupkg -s nuget.org -k %s" (string release.SemVer) (Environment.environVar "nugetkey")
    let result = DotNet.exec (fun a -> a |> withWorkDir "src/bin/Release") "run" args
    if (not result.OK) then
        failwith (result.Errors |> List.map (fun s -> s + ";") |> List.reduce (+))
    // runDotnet "netstandard/bin/Release" args
)


// --------------------------------------------------------------------------------------
// Generate the documentation
let gitName = "elmish"
let gitOwner = "elmish"
let gitHome = sprintf "https://github.com/%s" gitOwner

let fakePath = "packages" </> "build" </> "FAKE" </> "tools" </> "FAKE.exe"
let fakeStartInfo script workingDirectory args fsiargs environmentVars =
    (fun (info: ProcStartInfo) ->
        let env =
            seq [
                yield "MSBuild", DotNet.MSBuild.msBuildExe
                yield "GIT", Tools.Git.CommandHelper.gitPath
                yield "FSI", fsiPath
            ]
            |> Seq.append environmentVars
            |> Map.ofSeq
        { info with
            FileName = System.IO.Path.GetFullPath fakePath
            Arguments = sprintf "%s --fsiargs -d:FAKE %s \"%s\"" args fsiargs script
            WorkingDirectory = workingDirectory
            Environment = env }
    )

/// Run the given buildscript with FAKE.exe
let executeFAKEWithOutput workingDirectory script fsiargs envArgs =
    let exitCode =
        Process.execRaw
            (fakeStartInfo script workingDirectory "" fsiargs envArgs)
            TimeSpan.MaxValue false ignore ignore
    System.Threading.Thread.Sleep 1000
    exitCode

let copyFiles() =
    let header =
        Fake.Core.String.splitStr "\n" """(*** hide ***)
#I "../../src/bin/Release/netstandard2.0"
#r "Fable.Core.dll"
#r "Fable.PowerPack.dll"
#r "Fable.Elmish.dll"

(**
*)"""

    !!"src/*.fs"
    |> Seq.map (fun fn -> File.read fn |> Seq.append header, fn)
    |> Seq.iter (fun (lines,fn) ->
        let fsx = Path.Combine("docs/content",Path.ChangeExtension(fn |> Path.GetFileName, "fsx"))
        lines |> File.writeNew fsx)

// Documentation
let buildDocumentationTarget fsiargs target =
    Trace.trace (sprintf "Building documentation (%s), this could take some time, please wait..." target)
    let exit = executeFAKEWithOutput "docs/tools" "generate.fsx" fsiargs ["target", target]
    if exit <> 0 then
        failwith "generating reference documentation failed"
    ()

let generateHelp fail debug =
    copyFiles()
    Shell.cleanDir "docs/tools/.fake"
    let args =
        if debug then "--define:HELP"
        else "--define:RELEASE --define:HELP"
    try
        buildDocumentationTarget args "Default"
        Trace.traceImportant "Help generated"
    with
    | e when not fail ->
        Trace.traceImportant "generating help documentation failed"

Core.Target.create "GenerateDocs" (fun _ ->
    generateHelp true false
)

Core.Target.create "WatchDocs" (fun _ ->
    use watcher =
        (!! "docs/content/**/*.*")
        |> ChangeWatcher.run (fun changes -> generateHelp true true)

    Trace.traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.Dispose()
)

// --------------------------------------------------------------------------------------
// Release Scripts

Core.Target.create "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    Shell.cleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    Shell.copyRecursive "docs/output" tempDocsDir true |> Trace.tracefn "%A"
    Tools.Git.Staging.stageAll tempDocsDir
    Tools.Git.Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Branches.push tempDocsDir
)

#load "paket-files/build/fsharp/FAKE/modules/Octokit/Octokit.fsx"
open Octokit

Core.Target.create "Release" (fun _ ->
    let user =
        match Environment.environVarOrDefault "github-user" String.Empty with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> getUserInput "Username: "
    let pw =
        match Environment.environVarOrDefault "github-pw" String.Empty with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> getUserPassword "Password: "
    let remote =
        Tools.Git.CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    Tools.Git.Staging.stageAll ""
    Tools.Git.Commit.exec "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.pushBranch "" remote (Information.getBranchName "")

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" remote release.NugetVersion

    // release on github

    Fake.Api.GitHub.createClient user pw
    |> Fake.Api.GitHub.draftNewRelease gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    |> Fake.Api.GitHub.publishDraft
    |> Async.RunSynchronously
)

Core.Target.create "Publish" ignore

// Build order
"Clean"
  ==> "Meta"
//   ==> "InstallDotNetCore"
  ==> "Install"
  ==> "Build"
  ==> "Package"

"Build"
  ==> "GenerateDocs"
  ==> "ReleaseDocs"

"Publish"
  <== [ "Build"
        "Package"
        "PublishNuget"
        "ReleaseDocs" ]


// start build
Core.Target.runOrDefault "Build"
