"""Refresh the Demo roadmap from GitHub's native issue relationships (read only)."""

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess


REPOSITORY = "odradekk/train"
ROOT = 297
CONNECTIONS = {
    "labels": "name",
    "assignees": "login",
    "subIssues": "number repository { nameWithOwner }",
    "blockedBy": "number repository { nameWithOwner }",
}


def graphql(selection):
    query = 'query { repository(owner:"odradekk",name:"train") { ' + selection + ' } }'
    result = subprocess.run(
        ["gh", "api", "graphql", "--input", "-"],
        input=json.dumps({"query": query}), capture_output=True, text=True,
        encoding="utf-8", check=False,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "gh failed")
    response = json.loads(result.stdout)
    if response.get("errors"):
        raise RuntimeError(json.dumps(response["errors"], ensure_ascii=False))
    repository = response["data"]["repository"]
    if repository is None:
        raise RuntimeError("Repository is unavailable")
    return repository


def connection_query(name, cursor=None):
    after = ",after:" + json.dumps(cursor) if cursor else ""
    return f"{name}(first:100{after}) {{ nodes {{ {CONNECTIONS[name]} }} pageInfo {{ hasNextPage endCursor }} }}"


def read_batch(numbers):
    fields = "number title body state stateReason url updatedAt parent { number repository { nameWithOwner } } "
    fields += " ".join(connection_query(name) for name in CONNECTIONS)
    response = graphql(" ".join(f"i{number}:issue(number:{number}) {{ {fields} }}" for number in numbers))
    issues = []
    for number in numbers:
        issue = response[f"i{number}"]
        if issue is None:
            raise RuntimeError(f"Issue #{number} is unavailable")
        for name in CONNECTIONS:
            connection = issue[name]
            nodes = list(connection["nodes"])
            seen_cursors = set()
            while connection["pageInfo"]["hasNextPage"]:
                cursor = connection["pageInfo"]["endCursor"]
                if not cursor or cursor in seen_cursors:
                    raise RuntimeError(f"Invalid pagination for #{number} {name}")
                seen_cursors.add(cursor)
                connection = graphql(f"issue(number:{number}) {{ {connection_query(name, cursor)} }}")["issue"][name]
                nodes.extend(connection["nodes"])
            issue[name] = nodes
        related = issue["subIssues"] + issue["blockedBy"]
        if issue["parent"]:
            related.append(issue["parent"])
        for target in related:
            if target["repository"]["nameWithOwner"] != REPOSITORY:
                raise RuntimeError(f"Cross-repository relationship on #{number}: {target}")
        issues.append({
            key: issue[key] for key in
            ("number", "title", "body", "state", "stateReason", "url", "updatedAt")
        } | {
            "labels": [node["name"] for node in issue["labels"]],
            "assignees": [node["login"] for node in issue["assignees"]],
            "parent": issue["parent"]["number"] if issue["parent"] else None,
            "blockedBy": [node["number"] for node in issue["blockedBy"]],
            "children": [node["number"] for node in issue["subIssues"]],
        })
    return issues


def fetch():
    pending = [ROOT, 295]
    seen = set(pending)
    issues = []
    while pending:
        batch, pending = pending[:25], pending[25:]
        for issue in read_batch(batch):
            issues.append(issue)
            for number in issue["children"] + issue["blockedBy"]:
                if number not in seen:
                    seen.add(number)
                    pending.append(number)
    return {
        "repository": REPOSITORY, "root": ROOT,
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "issues": issues,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-only", type=Path, metavar="PATH", help="Write JSON without editing HTML")
    args = parser.parse_args()
    data = fetch()
    serialized = json.dumps(data, ensure_ascii=False, indent=2)
    if args.data_only:
        args.data_only.parent.mkdir(parents=True, exist_ok=True)
        args.data_only.write_text(serialized + "\n", encoding="utf-8")
        destination = args.data_only
    else:
        destination = Path(__file__).with_name("demo-roadmap.html")
        html = destination.read_text(encoding="utf-8")
        pattern = r'(<script id="roadmap-data" type="application/json">)[\s\S]*?(</script>)'
        if len(re.findall(pattern, html)) != 1:
            raise RuntimeError("Expected exactly one roadmap-data script element")
        safe = serialized.replace("&", "\\u0026").replace("<", "\\u003c").replace(">", "\\u003e").replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
        html = re.sub(pattern, lambda match: match[1] + "\n" + safe + "\n" + match[2], html)
        destination.write_text(html, encoding="utf-8")
    print(f"Refreshed {len(data['issues'])} issues: {destination}")


if __name__ == "__main__":
    main()
