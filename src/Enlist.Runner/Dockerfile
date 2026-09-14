# The enList runner image (docs/03-architecture/Container-Story.md §4.1).
#
# ONE generic image, versioned and shipped with enList itself — never per application. It contains
# only enlist-runner; the application arrives at run time as a read-only bind mount of its already
# extracted package directory, exactly the same directory the process backend hands the runner today.
# That is what lets the identical package digest run in-process on one agent and containerized on
# another, with no rebuild and no change to the artifact (§2.1).
#
# Build context is the REPOSITORY ROOT, not this directory, because the runner project has no
# PackageReferences but does live inside the solution:
#
#   docker build -f src/Enlist.Runner/Dockerfile -t enlist/runner:<version> .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Directory.Build.props FIRST, and it is not optional. MSBuild walks upward from the project
# directory looking for it, finds nothing inside this image without this line, and falls back to the
# SDK default version - so the runner inside every published image was stamped 1.0.0 while every
# runner built on a host was 3.0.0. That defeats the one job Directory.Build.props has ("one product
# version for every assembly in the tree, so nothing can drift"), and it drifted invisibly, because
# the image is the copy nobody rebuilds by accident.
COPY Directory.Build.props ./

# The runner's icon, which its csproj names as ..\branding\enlist.ico. Not optional either: without
# it the compiler stops with CS7064 before producing anything, which is how adding the icon on
# 2026-09-14 broke this image build while every host build passed - the host has the whole tree.
# A Linux runner has no use for a Win32 icon, but building the same project the same way everywhere
# is worth 30 KB.
COPY src/branding/ ./branding/

# Only the runner project is copied. It is guaranteed to have zero PackageReferences (its csproj
# enforces that with a build-time Error target), so this needs no restore of anything else and no
# NuGet access beyond the base image.
COPY src/Enlist.Runner/ ./Enlist.Runner/
RUN dotnet publish Enlist.Runner/Enlist.Runner.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# Runs as a non-root user: the runner only ever reads the application directory (mounted read-only)
# and speaks its control channel, so it has no reason to hold root inside the container. The package
# mount is read-only from the engine's side as well — belt and braces, since a plugin runs arbitrary
# author-supplied code and should not be able to modify the very files it was loaded from.
RUN useradd --create-home --uid 10001 enlist
USER enlist

COPY --from=build /publish /opt/enlist/runner

# Where the agent bind-mounts the application's extracted package directory.
WORKDIR /app

# No CMD arguments: the agent always supplies "--listen <port> --app /app". Deliberately no default
# port either — the agent picks one and must be the single source of truth for it, since it also has
# to publish that port and then dial it.
ENTRYPOINT ["dotnet", "/opt/enlist/runner/enlist-runner.dll"]
