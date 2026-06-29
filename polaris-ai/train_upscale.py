"""Thin wrapper: train the super-resolution model (RGB, pre-upsampling) via
train_task.

  python train_upscale.py --pairs data/own/upscale_tiles \
      --val-pairs data/own/upscale_val --scale 2 --base 64 --blocks 2 \
      --epochs 100 --out checkpoints/upscale
"""
import sys

import train_task

if __name__ == "__main__":
    if "--task" not in sys.argv:
        sys.argv += ["--task", "upscale"]
    train_task.main()
