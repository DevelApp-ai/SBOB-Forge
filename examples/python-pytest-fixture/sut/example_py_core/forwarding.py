"""The forwarding shape (SBOB-Forge #49): request-derived egress and temp-root
opens are identifiers, lifted through the declared netPolicies/pathPolicies
entries to tier-P wildcard entries."""


import os

import requests


def forward(target_url):
    return requests.get(target_url)


def save_snapshot(temp_root):
    os.makedirs(temp_root)
