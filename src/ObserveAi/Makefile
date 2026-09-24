# SAM invokes this through BuildMethod: makefile. The target name is
# build-<LogicalId> and ARTIFACTS_DIR is supplied by SAM.
#
# provided.al2023 runs the file named bootstrap at the artifact root, which is
# why the assembly is named bootstrap rather than renamed after publish.
build-TriageFunction:
	dotnet publish . --runtime linux-arm64 --configuration Release --output "$(ARTIFACTS_DIR)"
	rm -f "$(ARTIFACTS_DIR)"/*.dbg "$(ARTIFACTS_DIR)"/*.pdb
