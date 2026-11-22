# Remote Commander for the Linux live USB

The desktop autostart entry launches Remote Desktop Commander as the logged-in
live user. The launcher prevents duplicate instances, restarts after an exit,
and writes logs to `~/.local/state/wrongway-remote/remote.log`.

Bundle a trusted installed Commander package and all of its dependencies with
its compatible Node executable (the deployed USB uses Commander 0.3.4 and Node
22.23.1):

```sh
./build-overlay.sh /path/to/node /path/to/node_modules /path/to/zzzzzzz-wrongway-remote.squashfs
```

Place the resulting overlay in the live USB's `casper/` directory. Casper loads
it at the next boot. This operation adds files; it does not repartition the USB.
The desktop must have `flock` (util-linux), `xdg-open`, and a browser available.
No downloads are required at startup. First launch opens the device-pairing
page; complete that pairing once. The writable persistent home retains the
login. Credentials are never included in the overlay or repository.

Offline validation covers the relocated CLI, launcher syntax, restart and
single-instance behavior. Automatic connection after boot still needs a real
USB boot, network connectivity, and device pairing.
