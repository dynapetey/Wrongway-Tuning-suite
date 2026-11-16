WrongWay Tuning Live 0.4.8

Boot the USB's x64 UEFI entry. WrongWay Scanner starts automatically.
Open Tune BIN from the top toolbar to edit your calibration.

Save files under /home/wrongway to keep them across boots. The writable
file supplies about 4 GiB of persistent storage. Shut down before unplugging.

If the HP Compaq has an existing GRUB menu, press C and enter:
  search --no-floppy --label --set=root WRONGWAY
  configfile /boot/grub/grub.cfg
