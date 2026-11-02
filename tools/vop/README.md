# Reading VOP intersection assets

Scripts used to read how the Vanilla Overpass Project intersection assets
(`.crp`) are put together. They read files only; nothing is run from them.

- `paths.py FILE...`: the segments an intersection asset places (its
  `m_paths`): road, nodes, curve point, inverted, and the per-node traffic
  light / yield settings.
- `origin.py tools/vop FILE`: each node where a raised piece (ramp or slope)
  meets the ground pieces: the roads there, TM:PE's settings on each road end
  (pedestrian crossing, blocked junction, ...) and its lane connections.
- `nrbf.py`: reads the TM:PE records (a .NET BinaryFormatter stream) saved in
  the asset.

Run them with `python3 -I`.
- `netinfo.py FILE...`: a road asset's (`NetInfo`) settings: widths, corner
  offsets, terrain flags, and how many segment and node models it has. VOP's
  parts A and B have no node models and a corner offset of 0; its Two-Lane
  ramp lowers the ground but never flattens or clips it.
