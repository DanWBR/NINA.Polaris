# Network mode (Hotspot ↔ Station)

The Pi running Polaris is, by default, a **WiFi hotspot** named
`Polaris-Hotspot` with password `polaris1234`. That is what you
connect to with your phone, tablet, or laptop on the first night
in the field, before the Pi has ever seen your home WiFi.

Once you reach the Polaris UI, you can flip the Pi to **Station
mode** (joining your home WiFi like a normal client) right from
**Settings → Network**, the same idea as the ASIAIR PRO's
"WiFi Station Mode" toggle.

## First boot, from blank SD card to logged in

1. Flash Pi OS Bookworm 64-bit to an SD card.
2. Install the Polaris `.deb`
   (`sudo apt install ./polaris_arm64.deb`).
3. Reboot.
4. Wait ~30 seconds. On your phone, look for the WiFi network
   `Polaris-Hotspot`. Connect with password `polaris1234`.
5. Open `https://polaris-pi.local:5000` in your browser. Accept the
   self-signed certificate warning once.
6. The Polaris home screen loads.

You can stop here if you only need to use Polaris in the field
with no internet. The hotspot continues to work every boot, with
no further setup.

## Switching the Pi to your home WiFi

Useful at home, where the Pi can sit on your existing network and
also reach the internet (for plate-solving downloads, software
updates, time sync, etc.).

1. **Settings → Network (WiFi)**.
2. Click **Switch to Station Mode**.
3. Pick your home WiFi from the scan list. The list is sorted by
   signal strength; if your network is hidden (no broadcast SSID),
   pick **Other (hidden SSID)** and type the name.
4. Type the WiFi password (WPA2, 8 to 63 characters).
5. Click **Connect & switch**.

Polaris will drop the hotspot and bring up the new connection.
Because the browser you are using is still riding on the hotspot
WiFi link, you will see the connection drop the moment the switch
starts. That is expected.

To finish:

1. Reconnect your phone or laptop to your home WiFi network.
2. Reopen `https://polaris-pi.local:5000`. mDNS resolves the same
   hostname on the new network, so the URL does not change; only
   the IP under the hood does.

### What if the password is wrong?

Polaris waits up to 30 seconds for the Pi to actually get a DHCP
lease and reach the gateway on the new network. If it does not,
Polaris **automatically reverts to the hotspot**. You can
reconnect to `Polaris-Hotspot` and try again. The Pi never gets
stranded with no working WiFi.

The same auto-revert covers the case where the network exists but
the Pi is out of range, or where the AP is up but DHCP is broken.

## Switching back to hotspot mode

In Station mode you will see a **Switch back to Hotspot** button in
the same panel. Click it, then reconnect your phone/laptop to
`Polaris-Hotspot` to keep using Polaris.

## Changing the hotspot SSID or password

The defaults are public knowledge, so change them if your Pi will
sit on a roof somewhere a curious neighbour might wander past.

1. **Settings → Network → Edit hotspot SSID / password**.
2. Pick anything 1 to 32 characters for the SSID, and 8 to 63
   characters for the WPA2 password.
3. Save. The hotspot reboots with the new credentials.

Any device currently on the old SSID gets kicked off; reconnect
with the new one.

## What if Polaris does not show a Network panel?

The panel shows a banner with the reason:

- **"WiFi management requires Linux + NetworkManager"** — you are
  running Polaris on Windows or macOS. Manage WiFi via the OS
  settings on those hosts; the panel is read-only there.
- **"nmcli not installed"** — `sudo apt install network-manager`
  on the Pi, then reboot. The `.deb` declares this dependency,
  so a normal `apt install` of Polaris pulls it in.
- **"No WiFi interface detected"** — the host has no WiFi (a
  mini-PC with only Ethernet, for instance). The Polaris UI does
  not try to manage Ethernet; that lives in `/etc/network/` and
  `/etc/NetworkManager/system-connections/` per the host's normal
  config.

## Under the hood

Polaris drives `nmcli` via the `polaris` system user. The `.deb`
ships a PolicyKit rule
(`/etc/polkit-1/rules.d/50-polaris-nm.rules`) that grants only
that user, only `org.freedesktop.NetworkManager.*` actions, no
password prompts. Without the rule the daemon would get
`Not authorized` every time it tried to switch a connection.

The two NetworkManager connection names Polaris uses:

- **`polaris-hotspot`** — created on first boot by
  `polaris-wifi-bootstrap.service`, persists across reboots. AP
  mode, 2.4 GHz, `ipv4.method=shared` so connected devices get
  DHCP automatically.
- **`polaris-station`** — created on demand when you pick a
  network from the scan list. Replaced (delete + add) every time
  you click "Switch to Station Mode" so the credentials are
  always fresh.

WPA2 PSKs (your home WiFi password) live in
`/etc/NetworkManager/system-connections/*.nmconnection`, mode
600, root-owned. Polaris does not duplicate them into its own
profile.

## Edge cases worth knowing

- **2.4 GHz only on the hotspot.** Pi 5 has a 5 GHz radio but
  bringing up a 5 GHz AP runs into regulatory-domain
  complications that 2.4 GHz `band bg` does not. Maximum client
  compatibility wins for an "emergency hotspot in the field"
  feature.
- **Ethernet always works alongside.** When the Pi is plugged
  into Ethernet, NetworkManager keeps that route up regardless
  of the WiFi mode. Polaris just shows the WiFi side in the panel;
  Ethernet is unmanaged.
- **One WiFi NIC supported in v1.** If you plug a USB WiFi
  adapter into a Pi that also has the onboard radio, Polaris
  picks whichever NetworkManager lists first.
- **WPA3 not supported in v1.** Hotspot stays on WPA2 (broader
  client support); station mode also uses WPA2.

## See also

- [Raspberry Pi 4 / 5 setup](raspberry-pi-setup.md) — full
  end-to-end install, including the first-boot WiFi behaviour.
- [Installation](installation.md) — generic install across
  Linux + Windows.
