# Changelog fragments

One Markdown file per player-visible change, named in short lowercase words joined by hyphens
(`faster-hauling.md`). `release.py notes` renders the fragments added since the baseline tag into the
Steam change note; the fragments stay in this folder.

    category: fix
    notice: One optional sentence worth flagging to players.

    One or two plain sentences for a player: what they will see, not what the code does.

- Line 1 is required: `category:` followed by exactly one of `major`, `minor`, `qol`, `fix`.
- Line 2 is optional: `notice:` followed by one sentence for the read-this-first block of the release.
- No backticks, identifiers or file paths in the body.
- This README is not a fragment.
