"""Thin wrapper: train the star-halo-removal model (RGB 3->3) via train_task.

  python train_halo.py --pairs data/own/halo_tiles \
      --val-pairs data/own/halo_val --epochs 90 --out checkpoints/halo
"""
import sys

import train_task

if __name__ == "__main__":
    if "--task" not in sys.argv:
        sys.argv += ["--task", "halo"]
    train_task.main()
