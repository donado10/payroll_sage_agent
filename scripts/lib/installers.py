"""The Windows deliverables, built from a published app/ folder — run INSIDE the packaging container
(scripts/packaging.Dockerfile) by scripts/package.sh. Nothing here needs Windows; nothing here proves the
result on Windows either (the first install on a real server does).

    python3 scripts/lib/installers.py <stage> <version> <protocol>

  1. signs the agent exe (scripts/sign.sh: signed, or says « non signé »);
  2. the MSI: WiX source generated from app/, built by wixl, then what wixl cannot express added with
     msibuild — Windows Installer's own tables (delayed start, service SID, restart on failure, the data
     folder's rights) and the uninstall's purge — and ASSERTED table by table;
  3. signs the MSI;
  4. the zip (the dev fallback: install.ps1 / uninstall.ps1);
  5. the release file: version, protocol, signed or not, every deliverable's SHA-256.

[HARD] The UPGRADE CODE never changes: it is how a newer MSI finds the installed one and replaces it.
A component's GUID is derived from its file's name, so it is stable from build to build too.
"""

import datetime
import hashlib
import json
import os
import subprocess
import sys
import uuid
import zipfile

UPGRADE_CODE = "CE8575E7-CD73-49DF-9774-4DB83BD99A4C"
COMPONENT_NAMESPACE = uuid.UUID("60EC4013-A715-4DDA-8A7D-95128FE1387A")
SERVICE = "PayrollSageAgent"
DISPLAY = "IGF Paie - Agent Sage"
DESCRIPTION = "Relie les bases Sage 100 de ce serveur à la plateforme IGF Paie. Appelle la plateforme, n'ouvre aucun port."
REPO = "/repo"

# [HARD] The agent's custom actions run the exe by its PATH — type 34 (an exe, its working directory a Directory
# row) + 64 (return ignored) + 1024 (deferred) + 2048 (no impersonation: SYSTEM) = 3170. They were type 18
# (an exe from the File table) until 0.4.0, and Windows Installer refuses that with error 2753 « The File is not
# marked for installation » whenever the file is not being installed in THAT transaction: always on an
# uninstall (the purge), and on an install when the exe is already in place (measured: 0.4.0 on the user's
# Windows Server 2025, 2026-10-17). By path, the action runs whatever the file's own state.
AGENT_CA = 3170
AGENT_EXE = "\"[INSTALLDIR]PayrollSageAgent.exe\""
PURGE_CONDITION = 'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE AND NOT KEEPDATA'


def service_sid(name):
    """NT SERVICE\\<name>'s SID, as `sc showsid` computes it (ServiceIdentity.Sid, Get-ServiceSid)."""
    digest = hashlib.sha1(name.upper().encode("utf-16-le")).digest()
    return "S-1-5-80-" + "-".join(str(int.from_bytes(digest[i:i + 4], "little")) for i in range(0, 20, 4))


# Protected, no inheritance: SYSTEM and Administrators full, the service's own SID modify — by SID, so no
# account name is translated (« Administrateurs » on a French Windows). 0x1301bf = modify.
DATA_SDDL = f"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;{service_sid(SERVICE)})"


def run(*args, **kwargs):
    return subprocess.run(args, check=True, text=True, capture_output=True, **kwargs).stdout


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def sign(path):
    """The ONE signing step (scripts/sign.sh). True when the file is signed."""
    out = run("bash", f"{REPO}/scripts/sign.sh", path)
    print(out.strip())
    return os.environ.get("SIGNING_ENABLED") == "1"


def windows_text(source, target, version, protocol):
    """UTF-8 WITH a BOM (Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI) and CRLF (Notepad, SSMS) —
    except a .bat: cmd reads a BOM as part of its first command, so a .bat is plain ASCII."""
    text = open(source, encoding="utf-8").read()
    text = text.replace("{{VERSION}}", version).replace("{{PROTOCOL}}", protocol)
    text = text.replace("\r\n", "\n").replace("\n", "\r\n")
    encoding = "ascii" if source.endswith(".bat") else "utf-8-sig"
    with open(target, "w", encoding=encoding, newline="") as out:
        out.write(text)


def component_id(rel):
    return "C_" + "".join(ch if ch.isalnum() else "_" for ch in rel)


def wxs(stage, version, app_files, docs):
    def guid(rel):
        return str(uuid.uuid5(COMPONENT_NAMESPACE, rel)).upper()

    components = []
    for rel in app_files:
        name = os.path.basename(rel)
        file_id = "AgentExe" if name == f"{SERVICE}.exe" else component_id(rel).replace("C_", "F_", 1)
        service = ""
        if file_id == "AgentExe":
            service = f"""
              <ServiceInstall Id="AgentService" Name="{SERVICE}" DisplayName="[AGENTLABEL]" Description="{DESCRIPTION}"
                              Type="ownProcess" Start="auto" ErrorControl="normal" Account="NT SERVICE\\{SERVICE}" Vital="yes" />
              <ServiceControl Id="AgentControl" Name="{SERVICE}" Start="install" Stop="both" Remove="uninstall" Wait="yes" />"""
        components.append(f"""
            <Component Id="{component_id(rel)}" Guid="{guid(rel)}" Win64="yes">
              <File Id="{file_id}" Source="{stage}/{rel}" Name="{name}" KeyPath="yes" />{service}
            </Component>""")
    refs = "\n".join(f'      <ComponentRef Id="{component_id(rel)}" />' for rel in app_files)
    return f"""<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Product Id="*" Name="{DISPLAY}" Version="{version}" Manufacturer="IGF" Language="1036" Codepage="1252" UpgradeCode="{UPGRADE_CODE}">
    <Package InstallerVersion="500" Compressed="yes" InstallScope="perMachine" Description="{DISPLAY} {version}" Manufacturer="IGF" />
    <MajorUpgrade DowngradeErrorMessage="Une version plus récente de l'agent Sage est déjà installée." />
    <Media Id="1" Cabinet="agent.cab" EmbedCab="yes" />
    <Property Id="ARPNOMODIFY" Value="1" />
    <Directory Id="TARGETDIR" Name="SourceDir">
      <Directory Id="ProgramFiles64Folder">
        <Directory Id="IGFPROGRAMS" Name="IGF">
          <Directory Id="INSTALLDIR" Name="{SERVICE}">{"".join(components)}
          </Directory>
        </Directory>
      </Directory>
      <Directory Id="CommonAppDataFolder">
        <Directory Id="IGFDATA" Name="IGF">
          <Directory Id="DATADIR" Name="{SERVICE}">
            <Component Id="C_DataDir" Guid="{guid("data")}" Win64="yes">
              <CreateFolder />
              <RegistryValue Root="HKLM" Key="Software\\IGF\\{SERVICE}" Name="InstalledBy" Type="string" Value="msi" KeyPath="yes" />
              <RegistryValue Root="HKLM" Key="Software\\IGF\\{SERVICE}" Name="Version" Type="string" Value="{version}" />
              <RegistryValue Root="HKLM" Key="Software\\IGF\\{SERVICE}" Name="Label" Type="string" Value="[AGENTLABEL]" />
            </Component>
          </Directory>
        </Directory>
      </Directory>
    </Directory>
    <Feature Id="Agent" Level="1">
{refs}
      <ComponentRef Id="C_DataDir" />
    </Feature>
  </Product>
</Wix>
"""


def idt(path, columns, types, keys, rows):
    with open(path, "w", newline="\r\n") as f:
        f.write("\t".join(columns) + "\n" + "\t".join(types) + "\n" + "\t".join([os.path.basename(path)[:-4]] + keys) + "\n")
        for row in rows:
            f.write("\t".join(row) + "\n")


def table(msi, name):
    """The rows of one table, as lists of strings (msiinfo export, minus its 3 header lines)."""
    out = run("msiinfo", "export", msi, name)
    return [line.split("\t") for line in out.splitlines()[3:] if line]


def build_msi(stage, version, out_dir):
    work = os.path.join(out_dir, "msi-build")
    os.makedirs(work, exist_ok=True)
    app_files = sorted(os.path.relpath(os.path.join(d, f), stage) for d, _, fs in os.walk(os.path.join(stage, "app")) for f in fs)
    docs = ["LISEZMOI.txt", "compte-sql-lecture.sql", "acces-windows.sql", "exemple-liaison.json", "exemple-plusieurs-liaisons.json",
            "Configurer-agent.bat", "Redemarrer-agent.bat", "Mettre-a-jour-agent.bat"]
    files = app_files + docs
    source = os.path.join(work, "agent.wxs")
    with open(source, "w", encoding="utf-8") as f:
        f.write(wxs(stage, version, files, docs))
    msi = os.path.join(out_dir, f"{SERVICE}-{version}-x64.msi")
    run("wixl", "-a", "x64", "-o", msi, source)

    # What wixl cannot express. [!] Delayed start, the service SID and the failure actions were first
    # Windows Installer's own tables (MsiServiceConfig, MsiServiceConfigFailureActions, applied by
    # MsiConfigureServices): on the user's Windows Server 2025 that step failed with error 1939 and rolled
    # the install back (2026-10-17). They are now `PayrollSageAgent.exe configure-service`, run as SYSTEM
    # right after the service is created — the sc.exe commands install.ps1 ran there successfully. The data
    # folder's rights stay a table (MsiLockPermissionsEx): what WiX's own PermissionEx compiles to.
    idt(os.path.join(work, "MsiLockPermissionsEx.idt"),
        ["MsiLockPermissionsEx", "LockObject", "Table", "SDDLText", "Condition"],
        ["s72", "s72", "s32", "l0", "S255"], ["MsiLockPermissionsEx"],
        [["DataDirRights", "DATADIR", "CreateFolder", DATA_SDDL, ""]])
    run("msibuild", msi, "-i", os.path.join(work, "MsiLockPermissionsEx.idt"))

    sequence = {row[0]: row for row in table(msi, "InstallExecuteSequence")}
    for needed in ("StopServices", "DeleteServices", "InstallServices", "StartServices", "RemoveFiles"):
        if needed not in sequence:
            sys.exit(f"installers: wixl's InstallExecuteSequence has no {needed}")
    install_services = int(sequence["InstallServices"][2])
    start_services = int(sequence["StartServices"][2])
    stop_services = int(sequence["StopServices"][2])
    install_initialize = int(sequence["InstallInitialize"][2])
    queries = [
        # [HARD] The old version is removed INSIDE the new one's transaction (WiX's « afterInstallInitialize »):
        # an upgrade that fails is rolled back by Windows Installer to the version before. wixl schedules it
        # after InstallValidate (1401), where a failed upgrade leaves NEITHER version installed — the
        # self-update (Phase 9) cannot afford that. Component GUIDs are stable (derived from the file's name),
        # which this placement requires.
        f"UPDATE `InstallExecuteSequence` SET `Sequence` = {install_initialize + 1} WHERE `Action` = 'RemoveExistingProducts'",
        # Delayed start, service SID, restart on failure: the agent itself, as SYSTEM (deferred, no
        # impersonation), between InstallServices and StartServices, on install and upgrade — never on an
        # uninstall; it never fails the install (return ignored): AGENT_CA, as the purge.
        f"INSERT INTO CustomAction (Action, Type, Source, Target) VALUES ('AgentConfigure', {AGENT_CA}, 'INSTALLDIR', '{AGENT_EXE} configure-service')",
        f"INSERT INTO InstallExecuteSequence (Action, Condition, Sequence) VALUES ('AgentConfigure', 'NOT REMOVE~=\"ALL\"', {install_services + 50})",
        # A REAL uninstall — never the old product's removal during an upgrade — unpairs and deletes every
        # binding with its secrets (purge). As SYSTEM (no impersonation), deferred, and it never fails the
        # uninstall (return ignored): 18 exe from File + 64 continue + 1024 deferred + 2048 no impersonate.
        f"INSERT INTO CustomAction (Action, Type, Source, Target) VALUES ('AgentPurge', {AGENT_CA}, 'INSTALLDIR', '{AGENT_EXE} purge --confirm')",
        # KEEPDATA=1 (msiexec /x … KEEPDATA=1): the updater's rollback removes a version that does not work and
        # reinstalls the one before — the bindings must survive it, so the purge stands down.
        f"INSERT INTO InstallExecuteSequence (Action, Condition, Sequence) VALUES ('AgentPurge', '{PURGE_CONDITION}', {stop_services + 50})",
        # The services.msc label: AGENTLABEL on the command line (msiexec … AGENTLABEL="…"), else the one
        # saved by the installed version or by `set-label` (read BEFORE the old version is removed — AppSearch
        # at 50, RemoveExistingProducts at 1401), else the default. Type 51 sets a property. The search is
        # its own property, AGENTLABEL_SAVED: AppSearch would otherwise overwrite a label given on the command line.
        # agent.json, the agent's config in its own folder: once the service is started, `init-config` (as
        # SYSTEM) creates an empty one on a first install — IT fills it (Configurer-agent.bat) and applies it
        # (Redemarrer-agent.bat) —, or takes the agent.json placed NEXT TO the MSI and applies it at once.
        # OriginalDatabase is the MSI's own path, always set. Never fails the install (AGENT_CA), never on uninstall.
        f"INSERT INTO CustomAction (Action, Type, Source, Target) VALUES ('AgentInitConfig', {AGENT_CA}, 'INSTALLDIR', '{AGENT_EXE} init-config --msi \"[OriginalDatabase]\"')",
        f"INSERT INTO InstallExecuteSequence (Action, Condition, Sequence) VALUES ('AgentInitConfig', 'NOT REMOVE~=\"ALL\"', {start_services + 50})",
        "INSERT INTO `RegLocator` (`Signature_`, `Root`, `Key`, `Name`, `Type`) VALUES ('LabelSearch', 2, 'Software\\IGF\\PayrollSageAgent', 'Label', 18)",
        "INSERT INTO `AppSearch` (`Property`, `Signature_`) VALUES ('AGENTLABEL_SAVED', 'LabelSearch')",
        "INSERT INTO InstallExecuteSequence (Action, Condition, Sequence) VALUES ('AppSearch', '', 50)",
        "INSERT INTO CustomAction (Action, Type, Source, Target) VALUES ('LabelFromSaved', 51, 'AGENTLABEL', '[AGENTLABEL_SAVED]')",
        f"INSERT INTO CustomAction (Action, Type, Source, Target) VALUES ('LabelDefault', 51, 'AGENTLABEL', '{DISPLAY}')",
        "INSERT INTO InstallExecuteSequence (Action, Condition, Sequence) VALUES ('LabelFromSaved', 'NOT AGENTLABEL AND AGENTLABEL_SAVED', 51)",
        "INSERT INTO InstallExecuteSequence (Action, Condition, Sequence) VALUES ('LabelDefault', 'NOT AGENTLABEL', 52)",
    ]
    for query in queries:
        run("msibuild", msi, "-q", query)
    assert_msi(msi, version, files)
    return msi


def assert_msi(msi, version, files):
    """Fails the build unless every table the agent relies on is there, with the right rows."""
    problems = []

    def need(ok, what):
        if not ok:
            problems.append(what)

    services = table(msi, "ServiceInstall")
    need(any(r[1] == SERVICE and r[8] == f"NT SERVICE\\{SERVICE}" and r[4] == "2" for r in services), "ServiceInstall: the service, automatic, under its virtual account")
    need(any(r[1] == SERVICE and r[2] == "[AGENTLABEL]" for r in services), "ServiceInstall: the display name is AGENTLABEL")
    need(["AGENTLABEL_SAVED", "LabelSearch"] in table(msi, "AppSearch"), "AppSearch: the saved label")
    need(any(r[0] == "LabelSearch" and r[2] == "Software\\IGF\\PayrollSageAgent" and r[3] == "Label" and r[4] == "18" for r in table(msi, "RegLocator")), "RegLocator: HKLM 64-bit Label")
    control = table(msi, "ServiceControl")
    need(any(r[1] == SERVICE and r[2] == "163" for r in control), "ServiceControl: start at install, stop at install and uninstall, remove at uninstall (163)")
    need(any(r[1] == "DATADIR" and r[3] == DATA_SDDL for r in table(msi, "MsiLockPermissionsEx")), "MsiLockPermissionsEx: the data folder's rights")
    actions = {r[0]: r for r in table(msi, "CustomAction")}
    need(actions.get("AgentPurge", [None, None, None, None])[1:4] == [str(AGENT_CA), "INSTALLDIR", f"{AGENT_EXE} purge --confirm"], "CustomAction: the purge, by path")
    sequence = {r[0]: r for r in table(msi, "InstallExecuteSequence")}
    need("MsiConfigureServices" not in sequence, "InstallExecuteSequence: no MsiConfigureServices (error 1939 on Windows Server 2025)")
    need(actions.get("AgentConfigure", [None, None, None, None])[1:4] == [str(AGENT_CA), "INSTALLDIR", f"{AGENT_EXE} configure-service"], "CustomAction: configure-service, by path")
    need(actions.get("AgentInitConfig", [None, None, None, None])[1:4] == [str(AGENT_CA), "INSTALLDIR", f'{AGENT_EXE} init-config --msi "[OriginalDatabase]"'], "CustomAction: init-config, by path")
    need(not any(r[1] in ("3154", "18") or (r[1].isdigit() and int(r[1]) & 0x3F == 18) for r in table(msi, "CustomAction")), "CustomAction: no exe-from-File action (error 2753)")
    need(int(sequence.get("AgentInitConfig", ["", "", "0"])[2]) > int(sequence["StartServices"][2]) and sequence["AgentInitConfig"][1] == 'NOT REMOVE~="ALL"', "InstallExecuteSequence: init-config after StartServices, not on uninstall")
    need(int(sequence["InstallServices"][2]) < int(sequence.get("AgentConfigure", ["", "", "0"])[2]) < int(sequence["StartServices"][2])
         and sequence["AgentConfigure"][1] == 'NOT REMOVE~="ALL"', "InstallExecuteSequence: configure-service between InstallServices and StartServices, not on uninstall")
    need(sequence.get("AppSearch", ["", "", ""])[2] == "50", "InstallExecuteSequence: AppSearch at 50")
    need(sequence.get("LabelFromSaved", ["", ""])[1] == "NOT AGENTLABEL AND AGENTLABEL_SAVED" and sequence.get("LabelDefault", ["", ""])[1] == "NOT AGENTLABEL", "InstallExecuteSequence: the label, given, saved or default")
    need(actions.get("LabelDefault", [None, None, None, None])[1:4] == ["51", "AGENTLABEL", DISPLAY], "CustomAction: the default label")
    need(sequence.get("AgentPurge", ["", ""])[1] == PURGE_CONDITION, "InstallExecuteSequence: the purge, on a real uninstall only — not on an upgrade, not with KEEPDATA")
    need(int(sequence.get("RemoveExistingProducts", ["", "", "0"])[2]) == int(sequence["InstallInitialize"][2]) + 1,
         "InstallExecuteSequence: the old version removed inside the new one's transaction (a failed upgrade rolls back)")
    need(any(r[0] == "{" + UPGRADE_CODE + "}" for r in table(msi, "Upgrade")), "Upgrade: the permanent upgrade code")
    properties = {r[0]: r[1] for r in table(msi, "Property")}
    need(properties.get("ProductVersion") == version, "Property: ProductVersion")
    need(properties.get("ALLUSERS") == "1", "Property: per-machine")
    need(len(table(msi, "File")) == len(files), f"File: {len(files)} files")
    if problems:
        sys.exit("installers: the MSI is not what the agent needs —\n  " + "\n  ".join(problems))
    print(f"installers: MSI asserted — service, its label, its configuration by the agent, agent.json, the .bat files, folder rights, purge (KEEPDATA), upgrade in one transaction, upgrade code, {len(files)} files")


def main():
    stage, version, protocol = sys.argv[1], sys.argv[2], sys.argv[3]
    out_dir = os.path.dirname(stage)
    name = os.path.basename(stage)

    exe_signed = sign(os.path.join(stage, "app", f"{SERVICE}.exe"))
    for file in sorted(os.listdir(f"{REPO}/installer/windows")):
        windows_text(f"{REPO}/installer/windows/{file}", os.path.join(stage, file), version, protocol)

    msi = build_msi(stage, version, out_dir)
    msi_signed = sign(msi)

    files = sorted(os.path.relpath(os.path.join(d, f), stage) for d, _, fs in os.walk(stage) for f in fs)
    with open(os.path.join(stage, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\r\n") as out:
        for rel in files:
            out.write(f"{sha256(os.path.join(stage, rel))}  {rel.replace(os.sep, '/')}\n")
    archive = os.path.join(out_dir, name + ".zip")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel in files + ["SHA256SUMS.txt"]:
            z.write(os.path.join(stage, rel), f"{name}/{rel}")

    signed = exe_signed and msi_signed
    deliverables = []
    for path, kind in ((msi, "msi"), (archive, "zip")):
        digest = sha256(path)
        with open(path + ".sha256", "w") as out:
            out.write(f"{digest}  {os.path.basename(path)}\n")
        deliverables.append({"name": os.path.basename(path), "kind": kind, "bytes": os.path.getsize(path), "sha256": digest})
    release = {
        "version": version,
        "protocol": int(protocol),
        "signed": signed,
        "label": None if signed else "Version de développement, non signée",
        "builtAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "files": deliverables,
    }
    with open(os.path.join(out_dir, f"{SERVICE}-{version}.release.json"), "w") as out:
        json.dump(release, out, indent=2, ensure_ascii=False)
        out.write("\n")
    for d in deliverables:
        print(f"package: {d['name']} — {d['bytes'] / 1e6:.1f} MB, sha256 {d['sha256']}")
    print(f"package: {'SIGNED' if signed else 'NON SIGNÉ — version de développement'} · {SERVICE}-{version}.release.json")


if __name__ == "__main__":
    main()
