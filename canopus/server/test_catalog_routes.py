# N.I.N.A. Polaris, Canopus Assistant
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version.
#
# This program is distributed in the hope that it will be useful, but WITHOUT
# ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
# FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
# for more details. You should have received a copy of the license along with
# this program. If not, see <https://www.gnu.org/licenses/>.
#
# Every Polaris endpoint a catalog tool calls must exist in the Polaris API with
# the same method. The routes are read from the endpoint sources
# (MapGroup prefixes plus MapGet/MapPost/... routes), so a renamed or removed
# endpoint fails here instead of failing in front of a user.

import json
import os
import re

import pytest

_HERE = os.path.dirname(__file__)
_TOOLS = os.path.join(_HERE, "..", "shared", "tools")
_SRC = os.path.normpath(os.path.join(_HERE, "..", "..", "src", "NINA.Polaris"))

_GROUP = re.compile(r'(\w+)\s*=\s*\w+\.MapGroup\(\s*"([^"]*)"')
_ROUTE = re.compile(r'(\w+)\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]*)"')


def _norm(path):
    """/api/x/{id:int}/y -> /api/x/{}/y, without a trailing slash."""
    path = re.sub(r"\{[^}]*\}", "{}", path)
    return path.rstrip("/") or "/"


def _routes():
    routes = set()
    for root, _dirs, files in os.walk(_SRC):
        if os.sep + "bin" in root or os.sep + "obj" in root:
            continue
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(root, name), encoding="utf-8", errors="replace") as f:
                text = f.read()
            groups = {var: prefix for var, prefix in _GROUP.findall(text)}
            for var, verb, route in _ROUTE.findall(text):
                prefix = groups.get(var, "")
                full = route if route.startswith("/api") else prefix.rstrip("/") + "/" + route.lstrip("/")
                routes.add((verb.upper(), _norm(full)))
    return routes


def _calls(catalog):
    for t in catalog["tools"]:
        for key in ("polaris", "image"):
            block = t.get(key)
            if isinstance(block, dict) and block.get("path"):
                yield t["name"], block.get("method", "GET").upper(), block["path"]
        poll = t.get("poll")
        if isinstance(poll, dict) and poll.get("statusPath"):
            yield t["name"], "GET", poll["statusPath"]


@pytest.mark.skipif(not os.path.isdir(_SRC), reason="needs the Polaris sources beside canopus/")
def test_every_catalog_endpoint_exists_in_the_polaris_api():
    routes = _routes()
    assert len(routes) > 300, f"route scan found only {len(routes)} routes"
    with open(os.path.join(_TOOLS, "catalog.json"), encoding="utf-8") as f:
        catalog = json.load(f)
    missing = [(name, method, path) for name, method, path in _calls(catalog)
               if (method, _norm(path)) not in routes]
    assert not missing, f"catalog tools call endpoints that do not exist: {missing}"
