﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.CommandUtils;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;
using Microsoft.NET.Build.Containers.UnitTests;

namespace Microsoft.NET.Build.Containers.IntegrationTests;

[Collection("Docker tests")]
public class EndToEndTests
{
    private ITestOutputHelper _testOutput;

    public EndToEndTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    public static string NewImageName([CallerMemberName] string callerMemberName = "")
    {
        bool normalized = ContainerHelpers.NormalizeImageName(callerMemberName, out string? normalizedName);
        if (!normalized)
        {
            return normalizedName!;
        }

        return callerMemberName;
    }

    [DockerDaemonAvailableFact]
    public async Task ApiEndToEndWithRegistryPushAndPull()
    {
        string publishDirectory = BuildLocalApp();

        // Build the image

        Registry registry = new Registry(ContainerHelpers.TryExpandRegistryToUri(DockerRegistryManager.LocalRegistry));

        ImageBuilder imageBuilder = await registry.GetImageManifestAsync(
            DockerRegistryManager.BaseImage,
            DockerRegistryManager.Net6ImageTag,
            "linux-x64",
            ToolsetUtils.GetRuntimeGraphFilePath(),
            cancellationToken: default).ConfigureAwait(false);

        Assert.NotNull(imageBuilder);

        Layer l = Layer.FromDirectory(publishDirectory, "/app", false);

        imageBuilder.AddLayer(l);

        imageBuilder.SetEntryPoint(new[] { "/app/MinimalTestApp" });

        BuiltImage builtImage = imageBuilder.Build();

        // Push the image back to the local registry
        var sourceReference = new ImageReference(registry, DockerRegistryManager.BaseImage, DockerRegistryManager.Net6ImageTag);
        var destinationReference = new ImageReference(registry, NewImageName(), "latest");

        await registry.PushAsync(builtImage, sourceReference, destinationReference, Console.WriteLine, cancellationToken: default).ConfigureAwait(false);

        // pull it back locally
        new BasicCommand(_testOutput, "docker", "pull", $"{DockerRegistryManager.LocalRegistry}/{NewImageName()}:latest")
            .Execute()
            .Should().Pass();

        // Run the image
        new BasicCommand(_testOutput, "docker", "run", "--rm", "--tty", $"{DockerRegistryManager.LocalRegistry}/{NewImageName()}:latest")
            .Execute()
            .Should().Pass();
    }

    [DockerDaemonAvailableFact]
    public async Task ApiEndToEndWithLocalLoad()
    {
        string publishDirectory = BuildLocalApp();

        // Build the image

        Registry registry = new Registry(ContainerHelpers.TryExpandRegistryToUri(DockerRegistryManager.LocalRegistry));

        ImageBuilder imageBuilder = await registry.GetImageManifestAsync(
            DockerRegistryManager.BaseImage,
            DockerRegistryManager.Net6ImageTag,
            "linux-x64",
            ToolsetUtils.GetRuntimeGraphFilePath(),
            cancellationToken: default).ConfigureAwait(false);
        Assert.NotNull(imageBuilder);

        Layer l = Layer.FromDirectory(publishDirectory, "/app", false);

        imageBuilder.AddLayer(l);

        imageBuilder.SetEntryPoint(new[] { "/app/MinimalTestApp" });

        BuiltImage builtImage = imageBuilder.Build();

        // Load the image into the local Docker daemon
        var sourceReference = new ImageReference(registry, DockerRegistryManager.BaseImage, DockerRegistryManager.Net6ImageTag);
        var destinationReference = new ImageReference(registry, NewImageName(), "latest");

        await new LocalDocker(Console.WriteLine).LoadAsync(builtImage, sourceReference, destinationReference, default).ConfigureAwait(false);

        // Run the image
        new BasicCommand(_testOutput, "docker", "run", "--rm", "--tty", $"{NewImageName()}:latest")
            .Execute()
            .Should().Pass();
    }

    private string BuildLocalApp([CallerMemberName] string testName = "TestName", string tfm = "net6.0", string rid = "linux-x64")
    {
        string workingDirectory = Path.Combine(TestSettings.TestArtifactsDirectory, testName);

        DirectoryInfo d = new DirectoryInfo(Path.Combine(workingDirectory, "MinimalTestApp"));
        if (d.Exists)
        {
            d.Delete(recursive: true);
        }
        Directory.CreateDirectory(workingDirectory);

        new DotnetCommand(_testOutput, "new", "console", "-f", tfm, "-o", "MinimalTestApp")
            .WithWorkingDirectory(workingDirectory)
            .Execute()
            .Should().Pass();

        new DotnetCommand(_testOutput, "publish", "-bl", "MinimalTestApp", "-r", rid, "-f", tfm)
            .WithWorkingDirectory(workingDirectory)
            .Execute()
            .Should().Pass();

        string publishDirectory = Path.Join(workingDirectory, "MinimalTestApp", "bin", "Debug", tfm, rid, "publish");
        return publishDirectory;
    }

    [DockerDaemonAvailableFact]
    public async Task EndToEnd_NoAPI()
    {
        DirectoryInfo newProjectDir = new DirectoryInfo(Path.Combine(TestSettings.TestArtifactsDirectory, "CreateNewImageTest"));
        DirectoryInfo privateNuGetAssets = new DirectoryInfo(Path.Combine(TestSettings.TestArtifactsDirectory, "ContainerNuGet"));

        if (newProjectDir.Exists)
        {
            newProjectDir.Delete(recursive: true);
        }

        if (privateNuGetAssets.Exists)
        {
            privateNuGetAssets.Delete(recursive: true);
        }

        newProjectDir.Create();
        privateNuGetAssets.Create();
        var repoGlobalJson = Path.Combine("..", "..", "..", "..", "global.json");
        File.Copy(repoGlobalJson, Path.Combine(newProjectDir.FullName, "global.json"));

        var packagedir = new DirectoryInfo(CurrentFile.Relative("./package"));

        // do not pollute the primary/global NuGet package store with the private package(s)
        var env = new (string, string)[] { new("NUGET_PACKAGES", privateNuGetAssets.FullName) };
        // 🤢
        FileInfo[] nupkgs = packagedir.GetFiles("*.nupkg");
        if (nupkgs == null || nupkgs.Length == 0)
        {
            // Build Microsoft.NET.Build.Containers.csproj & wait.
            // for now, fail.
            Assert.Fail("No nupkg found in expected package folder. You may need to rerun the build");
        }


        new DotnetCommand(_testOutput, "new", "webapi", "-f", "net7.0")
            .WithWorkingDirectory(newProjectDir.FullName)
            // do not pollute the primary/global NuGet package store with the private package(s)
            .WithEnvironmentVariable("NUGET_PACKAGES", privateNuGetAssets.FullName)
            .Execute()
            .Should().Pass();

        new DotnetCommand(_testOutput, "new", "nugetconfig")
            .WithWorkingDirectory(newProjectDir.FullName)
            .Execute()
            .Should().Pass();

        new DotnetCommand(_testOutput, "nuget", "add", "source", packagedir.FullName, "--name", "local-temp")
            .WithEnvironmentVariable("NUGET_PACKAGES", privateNuGetAssets.FullName)
            .WithWorkingDirectory(newProjectDir.FullName)
            .Execute()
            .Should().Pass();

        // Add package to the project
        new DotnetCommand(_testOutput, "add", "package", "Microsoft.NET.Build.Containers", "--prerelease", "-f", "net7.0")
            .WithEnvironmentVariable("NUGET_PACKAGES", privateNuGetAssets.FullName)
            .WithWorkingDirectory(newProjectDir.FullName)
            .Execute()
            .Should().Pass();

        string imageName = NewImageName();
        string imageTag = "1.0";

        // Build & publish the project
        new DotnetCommand(
            _testOutput,
            "publish",
            "/p:publishprofile=DefaultContainer",
            "/p:runtimeidentifier=linux-x64",
            "/bl",
            $"/p:ContainerBaseImage={DockerRegistryManager.FullyQualifiedBaseImageDefault}",
            $"/p:ContainerRegistry={DockerRegistryManager.LocalRegistry}",
            $"/p:ContainerImageName={imageName}",
            $"/p:Version={imageTag}")
            .WithEnvironmentVariable("NUGET_PACKAGES", privateNuGetAssets.FullName)
            .WithWorkingDirectory(newProjectDir.FullName)
            .Execute()
            .Should().Pass();

        new BasicCommand(_testOutput, "docker", "pull", $"{DockerRegistryManager.LocalRegistry}/{imageName}:{imageTag}")
            .Execute()
            .Should().Pass();

        var containerName = "test-container-1";
        CommandResult processResult = new BasicCommand(
            _testOutput,
            "docker",
            "run",
            "--rm",
            "--name",
            containerName,
            "--publish",
            "5017:80",
            "--detach",
            $"{DockerRegistryManager.LocalRegistry}/{imageName}:{imageTag}")
        .Execute();
        processResult.Should().Pass();
        Assert.NotNull(processResult.StdOut);

        string appContainerId = processResult.StdOut.Trim();

        bool everSucceeded = false;

        HttpClient client = new();

        // Give the server a moment to catch up, but no more than necessary.
        for (int retry = 0; retry < 10; retry++)
        {
            try
            {
                var response = await client.GetAsync("http://localhost:5017/weatherforecast").ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    everSucceeded = true;
                    break;
                }
            }
            catch { }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        new BasicCommand(_testOutput, "docker", "logs", appContainerId)
            .Execute()
            .Should().Pass();

        Assert.True(everSucceeded, "http://localhost:5017/weatherforecast never responded.");

        new BasicCommand(_testOutput, "docker", "stop", appContainerId)
            .Execute()
            .Should().Pass();

        newProjectDir.Delete(true);
        privateNuGetAssets.Delete(true);
    }

    [DockerSupportsArchInlineData("linux/arm/v7", "linux-arm", "/app")]
    [DockerSupportsArchInlineData("linux/arm64/v8", "linux-arm64", "/app")]
    [DockerSupportsArchInlineData("linux/386", "linux-x86", "/app", Skip="There's no apphost for linux-x86 so we can't execute self-contained, and there's no .NET runtime base image for linux-x86 so we can't execute framework-dependent.")]
    [DockerSupportsArchInlineData("windows/amd64", "win-x64", "C:\\app")]
    [DockerSupportsArchInlineData("linux/amd64", "linux-x64", "/app")]
    [DockerDaemonAvailableTheory]
    public async Task CanPackageForAllSupportedContainerRIDs(string dockerPlatform, string rid, string workingDir)
    {
        string publishDirectory = BuildLocalApp(tfm: "net7.0", rid: rid);

        // Build the image
        Registry registry = new(ContainerHelpers.TryExpandRegistryToUri(DockerRegistryManager.BaseImageSource));

        ImageBuilder? imageBuilder = await registry.GetImageManifestAsync(
            DockerRegistryManager.BaseImage,
            DockerRegistryManager.Net7ImageTag,
            rid,
            ToolsetUtils.GetRuntimeGraphFilePath(),
            cancellationToken: default).ConfigureAwait(false);
        Assert.NotNull(imageBuilder);

        Layer l = Layer.FromDirectory(publishDirectory, "/app", false);

        imageBuilder.AddLayer(l);
        imageBuilder.SetWorkingDirectory(workingDir);

        string[] entryPoint = DecideEntrypoint(rid, "MinimalTestApp", workingDir);
        imageBuilder.SetEntryPoint(entryPoint);

        BuiltImage builtImage = imageBuilder.Build();

        // Load the image into the local Docker daemon
        var sourceReference = new ImageReference(registry, DockerRegistryManager.BaseImage, DockerRegistryManager.Net7ImageTag);
        var destinationReference = new ImageReference(registry, NewImageName(), rid);
        await new LocalDocker(Console.WriteLine).LoadAsync(builtImage, sourceReference, destinationReference, default).ConfigureAwait(false);

        // Run the image
        new BasicCommand(
            _testOutput,
            "docker",
            "run",
            "--rm",
            "--tty",
            "--platform",
            dockerPlatform,
            $"{NewImageName()}:{rid}")
            .Execute()
            .Should()
            .Pass();

        string[] DecideEntrypoint(string rid, string appName, string workingDir)
        {
            var binary = rid.StartsWith("win", StringComparison.Ordinal) ? $"{appName}.exe" : appName;
            return new[] { $"{workingDir}/{binary}" };
        }
    }
}
