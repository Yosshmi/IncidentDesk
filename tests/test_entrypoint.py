"""Check container startup orchestration without a container engine."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class EntrypointTests(unittest.TestCase):
    def run_entrypoint(self, *, bootstrap=False, fail_migration=False, args=()):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            executable = root / "dotnet"
            executable.write_text(
                "#!/usr/bin/env python3\n"
                "import json, os, sys\n"
                "with open(os.environ['CALL_LOG'], 'a') as log:\n"
                "    log.write(json.dumps({'args':sys.argv[1:], 'port':os.environ.get('ASPNETCORE_HTTP_PORTS')}) + '\\n')\n"
                "if '--migrate' in sys.argv and os.environ.get('FAIL_MIGRATION') == 'true':\n"
                "    sys.exit(7)\n"
            )
            executable.chmod(0o755)
            environment = {
                "PATH": f"{root}{os.pathsep}{os.environ['PATH']}",
                "CALL_LOG": str(root / "calls.jsonl"),
                "PORT": "10000",
                "INCIDENTDESK_BOOTSTRAP_DEMO": str(bootstrap).lower(),
                "FAIL_MIGRATION": str(fail_migration).lower(),
            }
            script = Path(__file__).resolve().parents[1] / "scripts/docker-entrypoint.sh"
            result = subprocess.run(["/bin/sh", str(script), *args], env=environment, capture_output=True, text=True, check=False)
            log = root / "calls.jsonl"
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            return result, calls

    def test_normal_start_preserves_arguments_and_binds_render_port(self):
        result, calls = self.run_entrypoint(args=("--urls", "http://localhost:1234"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([{"args": ["IncidentDesk.Api.dll", "--urls", "http://localhost:1234"], "port": "10000"}], calls)

    def test_public_demo_applies_migrations_and_seed_before_serving(self):
        result, calls = self.run_entrypoint(bootstrap=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([
            {"args": ["IncidentDesk.Api.dll", "--migrate", "--seed-demo"], "port": "10000"},
            {"args": ["IncidentDesk.Api.dll"], "port": "10000"},
        ], calls)

    def test_failed_migration_prevents_server_start(self):
        result, calls = self.run_entrypoint(bootstrap=True, fail_migration=True)
        self.assertEqual(7, result.returncode)
        self.assertEqual(1, len(calls))

    def test_explicit_migration_command_does_not_trigger_a_second_bootstrap(self):
        result, calls = self.run_entrypoint(bootstrap=True, args=("--migrate", "--seed-demo"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([{"args": ["IncidentDesk.Api.dll", "--migrate", "--seed-demo"], "port": "10000"}], calls)


if __name__ == "__main__":
    unittest.main()
