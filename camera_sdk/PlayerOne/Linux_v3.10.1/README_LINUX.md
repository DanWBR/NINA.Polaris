### The PlayerOneCamera SDK on Linux
Note: If you have any problems, please contact us: support@player-one-astronomy.com

#### The libPlayerOneCamera.so relies on libusb-1.0.0.so

Please make sure the libusb-1.0 library already exists on your system, or if not, install it using the command:
(root permission may be required)

##### Debian/Ubuntu/ PI OS

apt-get install libusb-1.0-0

##### Fedora

dnf install libusb-1.0

##### Arch Linux / Manjaro

pacman -S libusb

#### The udev rules

Please put '99-player_one_astronomy.rules' in the'/lib/udev/rules.d/' or '/etc/udev/rules.d/', you can install it using the command:

sudo install 99-player_one_astronomy.rules /lib/udev/rules.d/

