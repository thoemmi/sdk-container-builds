﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using containerize;

#pragma warning disable CA1852

try
{
    return await new ContainerizeCommand().InvokeAsync(args).ConfigureAwait(false);
}
catch (Exception e)
{
    string message = !e.Message.StartsWith("CONTAINER", StringComparison.OrdinalIgnoreCase) ? $"CONTAINER9000: " + e.ToString() : e.ToString();
    Console.WriteLine($"Containerize: error {message}");

    return 1;
}
