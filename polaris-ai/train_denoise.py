"""Thin wrapper: train the denoise model (RGB 3->3) via train_task.

  python train_denoise.py --pairs data/own/denoise_tiles \
      --val-pairs data/own/denoise_val --epochs 80 --out checkpoints/denoise
"""
import sys

import train_task

if __name__ == "__main__":
    if "--task" not in sys.argv:
        sys.argv += ["--task", "denoise"]
    train_task.main()
