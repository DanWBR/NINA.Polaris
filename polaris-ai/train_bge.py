"""Thin wrapper: train the BGE model (RGB 3->3, predicts the background plane)
via train_task.

  python train_bge.py --pairs data/own/bge_tiles \
      --val-pairs data/own/bge_val --epochs 120 --out checkpoints/bge
"""
import sys

import train_task

if __name__ == "__main__":
    if "--task" not in sys.argv:
        sys.argv += ["--task", "bge"]
    train_task.main()
