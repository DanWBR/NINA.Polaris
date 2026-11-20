# SMBClient (vendored)

These files are `Sources/SMBClient` of
[kishikawakatsumi/SMBClient](https://github.com/kishikawakatsumi/SMBClient) at
commit `66eafaa6d17e034e8036dee4b3ebc1b52cb53919`, unchanged, under the MIT
license in `LICENSE` next to this file.

SMBClient ships only as a Swift package, and Capacitor 6 builds plugins with
CocoaPods, so the sources are compiled into the PolarisSmb pod. The commit is
newer than the last tag (0.3.1) because it carries the fix for a connection
that hung on the wrong payload size, and the keep-alive.

To update, replace this folder with `Sources/SMBClient` of the new commit, keep
`LICENSE` and this file, and change the commit above.
