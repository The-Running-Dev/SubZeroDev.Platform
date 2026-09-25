#Requires -Version 7.0
#Requires -Modules Pester

<#
  Test-DesignDrift.ps1 exits the process on every path (0/1/2), which would kill the Pester
  runner if invoked with `&`. Same structure as Wait-PullRequestCheck.Tests.ps1: the script
  guards its exit-calling wrapper with `$MyInvocation.InvocationName -ne '.'`, so dot-sourcing
  defines its functions here and skips the wrapper. `Invoke-DriftCheck` is called directly and
  asserted on its returned result; `Get-DriftExitCode` is a pure state->code map tested alone.

  The two boundaries - the tracker and git - are mocked at their own functions rather than at
  `gh` and `git`, because both read a native *exit code* as their answer and a mock cannot set
  $LASTEXITCODE. Mocking the seam above them tests the comparison, which is what this script is.

  Every slices document is written into $TestDrive; none of these tests read the real one, so
  they do not start failing when a slice lands.
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Test-DesignDrift.ps1'
    # The dot-source runs the script's own Set-StrictMode/$ErrorActionPreference in this scope.
    # Captured here and restored in the matching AfterAll so they do not leak into whichever
    # test file Pester runs next in the same invocation.
    $script:PreDotSourceErrorActionPreference = $ErrorActionPreference
    . $script:ScriptPath

    function New-SlicesDoc {
        param([Parameter(Mandatory)][string] $Content, [string] $Name = 'slices.md')
        $path = Join-Path $TestDrive $Name
        Set-Content -LiteralPath $path -Value $Content -Encoding utf8
        $path
    }

    function New-Issue {
        param([int] $Number, [string] $Title, [string] $Body)
        [pscustomobject]@{ number = $Number; title = $Title; state = 'OPEN'; body = $Body }
    }

    function New-Tracker {
        param([object[]] $Issues = @())
        [pscustomobject]@{ Issues = @($Issues); Failure = $null }
    }

    # One slice, two criteria - the shape every positive case starts from.
    $script:TwoCriterionDoc = @'
# Slices

## Outstanding

## S1 — A slice

Delivers: something a reader can follow.

Acceptance:
  - S1.1 The first criterion holds.
  - S1.2 The second criterion holds.
'@

    # The same slice with bold ids, the form /track writes into an issue body and a slices
    # document commonly copies.
    $script:TwoCriterionDocBoldIds = @'
# Slices

## Outstanding

## S1 — A slice

Acceptance:
  - **S1.1** The first criterion holds.
  - **S1.2** The second criterion holds.
'@

    # A slices document whose title names its effort. Declared here rather than in its Context:
    # Pester runs a Context body at discovery, before any BeforeAll has run.
    $script:TaggedDoc = @'
# Slices — commercial (D5)

## Outstanding

## S1 — A slice

Acceptance:
  - **S1.1** The first criterion holds.
  - **S1.2** The second criterion holds.
'@
}

AfterAll {
    $ErrorActionPreference = $script:PreDotSourceErrorActionPreference
    Set-StrictMode -Off
}

Describe 'Test-DesignDrift' {

    Context 'criterion ids' {

        It 'matching ids on both sides is Clean, exit 0' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body "### Done when`n- [ ] **S1.1** first`n- [x] **S1.2** second"
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Clean'
            $r.Findings.Count | Should -Be 0
            $r.SlicesCompared | Should -Be 1
            Get-DriftExitCode -State $r.State | Should -Be 0
        }

        It 'a slice nested as a third-level heading under Outstanding is compared, not silently dropped' {
            # design/90-decisions.md, 2026-08-30: slices from S19 on sit as `### S<n>` under the
            # `## Outstanding` section rather than as their own `## S<n>` section. A parser that
            # only recognises `##` sees zero slices here and reports Clean for the wrong reason.
            $path = New-SlicesDoc -Content @'
# Slices

## Outstanding

### S19 — A slice

Delivers: something a reader can follow.

Acceptance:
  - S19.1 The first criterion holds.
  - S19.2 The second criterion holds.

## Landed
'@
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 171 -Title 'S19 — A slice' -Body "- [ ] **S19.1** first`n- [ ] **S19.2** second"
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Clean'
            $r.SlicesCompared | Should -Be 1
        }

        It 'bold criterion ids in the document are read like plain ones' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDocBoldIds
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body "### Done when`n- [ ] **S1.1** first`n- [x] **S1.2** second"
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Clean'
            $r.Findings.Count | Should -Be 0
            $r.SlicesCompared | Should -Be 1
        }

        It 'reworded criteria with the same ids are not drift' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body "- [ ] **S1.1** completely different wording here`n- [ ] **S1.2** and here too"
            ) }

            (Invoke-DriftCheck -SlicesPath $path).State | Should -Be 'Clean'
        }

        It 'an id in the doc but not the issue is reported as InDocNotIssue, exit 1' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body '- [ ] **S1.1** first'
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Drifted'
            $r.Findings.Kind | Should -Contain 'InDocNotIssue'
            ($r.Findings | Where-Object Kind -eq 'InDocNotIssue').Detail | Should -Be 'S1.2'
            Get-DriftExitCode -State $r.State | Should -Be 1
        }

        It 'an id in the issue but not the doc - a renumber - is reported as InIssueNotDoc' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body "- [ ] **S1.1** first`n- [x] **S1.7** a tick now pointing at something else"
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Drifted'
            ($r.Findings | Where-Object Kind -eq 'InIssueNotDoc').Detail | Should -Be 'S1.7'
        }

        It 'an id cited in prose outside a slice section is not counted as a criterion' {
            # The real document does exactly this in its Contract questions section, so a
            # whole-file regex would invent criteria that were never cut.
            $path = New-SlicesDoc -Content @'
# Slices

## Contract questions

None outstanding - zero checks yields NotEvaluated, exercised by S1.9, and the batch by S1.8.

## S1 — A slice

Acceptance:
  - S1.1 The only real criterion.
'@
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body '- [ ] **S1.1** the only real criterion'
            ) }

            (Invoke-DriftCheck -SlicesPath $path).State | Should -Be 'Clean'
        }

        It 'a landed slice with no body is not reported as a removal' {
            $path = New-SlicesDoc -Content @'
# Slices

## Outstanding

None.

## Landed

| Slice | Name | Issue | Criteria | Body complete at |
|---|---|---|---|---|
| **S1** | A slice that landed | #9, closed | S1.1-S1.2 | `af610a6` |
'@
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice that landed' -Body "- [x] **S1.1** first`n- [x] **S1.2** second"
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Clean'
            $r.Findings.Count | Should -Be 0
            $r.SlicesCompared | Should -Be 0
        }

        It 'a slice with no issue at all is reported rather than skipped' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue { New-Tracker }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Drifted'
            $r.Findings.Kind | Should -Contain 'NoIssue'
        }
    }

    Context 'slice issue lookup' {

        BeforeEach {
            $script:SliceLookupDoc = New-SlicesDoc -Content @'
# Slices

## S3 — Close the laptop, open the phone

Acceptance:
  - S3.1 The first criterion holds.
  - S3.3 Spill catch-up completes.
'@
            # The newer criterion bug precedes the closed slice issue in tracker order.
            $script:CriterionBug = New-Issue -Number 300 -Title 'S3.3 spill-catch-up test intermittently times out' -Body 'Investigate the intermittent timeout.'
            $script:SliceIssue = New-Issue -Number 4 -Title 'S3 — Close the laptop, open the phone' -Body "- [x] **S3.1** first`n- [x] **S3.3** spill catch-up"
            $script:SliceIssue.state = 'CLOSED'
        }

        It 'selects the real slice issue even when a criterion bug appears first' {
            Mock Get-TrackerIssue { New-Tracker -Issues @($script:CriterionBug, $script:SliceIssue) }

            $r = Invoke-DriftCheck -SlicesPath $script:SliceLookupDoc

            $r.State | Should -Be 'Clean'
            $r.SlicesCompared | Should -Be 1
            $r.Findings.Count | Should -Be 0
            $r.Failures.Count | Should -Be 0
            Get-DriftExitCode -State $r.State | Should -Be 0
        }

        It 'attributes genuine drift to the real slice issue, not the criterion bug' {
            $script:SliceIssue.body = '- [x] **S3.1** first'
            Mock Get-TrackerIssue { New-Tracker -Issues @($script:CriterionBug, $script:SliceIssue) }

            $r = Invoke-DriftCheck -SlicesPath $script:SliceLookupDoc

            $r.State | Should -Be 'Drifted'
            $r.SlicesCompared | Should -Be 1
            $r.Findings.Count | Should -Be 1
            $r.Findings[0].Kind | Should -Be 'InDocNotIssue'
            $r.Findings[0].Detail | Should -Be 'S3.3'
            $r.Findings[0].Issue | Should -Be 4
            Get-DriftExitCode -State $r.State | Should -Be 1
        }

        It 'reports NoIssue when only the criterion bug exists' {
            Mock Get-TrackerIssue { New-Tracker -Issues @($script:CriterionBug) }

            $r = Invoke-DriftCheck -SlicesPath $script:SliceLookupDoc

            $r.State | Should -Be 'Drifted'
            $r.SlicesCompared | Should -Be 0
            $r.Findings.Count | Should -Be 1
            $r.Findings[0].Kind | Should -Be 'NoIssue'
            $r.Findings[0].Issue | Should -Be 0
            Get-DriftExitCode -State $r.State | Should -Be 1
        }
    }

    Context 'effort-qualified titles' {

        BeforeEach {
            # A retired effort's closed S1, numbered from 1 like every effort, and listed first.
            $script:RetiredS1 = New-Issue -Number 8 -Title 'S1 — A retired effort''s first slice' -Body '- [x] **S1.7** something else entirely'
            $script:RetiredS1.state = 'CLOSED'
        }

        It 'a tagged document matches its own effort''s issue, not a retired effort''s of the same number' {
            $path = New-SlicesDoc -Content $script:TaggedDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                $script:RetiredS1,
                (New-Issue -Number 9 -Title 'D5-S1 — A slice' -Body "- [ ] **S1.1** first`n- [ ] **S1.2** second")
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'Clean'
            $r.SlicesCompared | Should -Be 1
        }

        It 'a tagged document with only an unqualified issue reports NoIssue rather than borrowing it' {
            $path = New-SlicesDoc -Content $script:TaggedDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @($script:RetiredS1) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.Findings.Count | Should -Be 1
            $r.Findings[0].Kind | Should -Be 'NoIssue'
            $r.SlicesCompared | Should -Be 0
        }

        It 'an untagged document still matches the unqualified title' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body "- [ ] **S1.1** first`n- [ ] **S1.2** second"
            ) }

            (Invoke-DriftCheck -SlicesPath $path).State | Should -Be 'Clean'
        }

        It 'an explicit -EffortTag qualifies the match over the document''s own' {
            $path = New-SlicesDoc -Content $script:TaggedDoc
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                (New-Issue -Number 9 -Title 'D5-S1 — A slice' -Body '- [ ] **S1.9** not this one'),
                (New-Issue -Number 10 -Title 'G1-S1 — A slice' -Body "- [ ] **S1.1** first`n- [ ] **S1.2** second")
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path -EffortTag 'G1'

            $r.State | Should -Be 'Clean'
            $r.SlicesCompared | Should -Be 1
        }

        It 'a parenthesised tag below the title is prose, not the effort' {
            $path = New-SlicesDoc -Content @'
# Slices

## S1 — A slice (D5)
'@
            Get-EffortTag -Path $path | Should -BeNullOrEmpty
        }
    }

    Context 'pin ancestry' {

        BeforeEach {
            $script:PinnedDoc = New-SlicesDoc -Content $script:TwoCriterionDoc -Name 'pinned.md'
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' `
                    -Body "- [ ] **S1.1** first`n- [ ] **S1.2** second`n<!-- agent:start -->`nScope and criteria: ``design/30-slices.md`` § S1 @ ``deadbee```n<!-- agent:end -->"
            ) }
        }

        It 'a pin that is an ancestor of HEAD is Clean' {
            Mock Test-CommitIsAncestor { 'Ancestor' }

            $r = Invoke-DriftCheck -SlicesPath $script:PinnedDoc

            $r.State | Should -Be 'Clean'
            Should -Invoke Test-CommitIsAncestor -Exactly -Times 1
        }

        It 'a pin that is not an ancestor is drift, exit 1' {
            Mock Test-CommitIsAncestor { 'NotAncestor' }

            $r = Invoke-DriftCheck -SlicesPath $script:PinnedDoc

            $r.State | Should -Be 'Drifted'
            ($r.Findings | Where-Object Kind -eq 'PinNotAncestor').Detail | Should -Be 'deadbee'
            Get-DriftExitCode -State $r.State | Should -Be 1
        }

        It 'a pin this clone cannot resolve is NOT clean and NOT drift - it is exit 2' {
            # The distinction I12 exists for: absence of an answer never becomes an answer.
            Mock Test-CommitIsAncestor { 'Unresolvable' }

            $r = Invoke-DriftCheck -SlicesPath $script:PinnedDoc

            $r.State | Should -Be 'NotEvaluated'
            $r.Findings.Count | Should -Be 0
            $r.Failures.Reason | Should -Contain 'PinUnresolvable'
            Get-DriftExitCode -State $r.State | Should -Be 2
        }
    }

    Context 'incomplete runs never report clean' {

        It 'an unreadable tracker is NotEvaluated, exit 2' {
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc
            Mock Get-TrackerIssue {
                [pscustomobject]@{ Issues = @(); Failure = (New-Failure -Reason 'GhUnavailable' -Detail 'gh exited 4') }
            }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'NotEvaluated'
            $r.Failures.Reason | Should -Contain 'GhUnavailable'
            Get-DriftExitCode -State $r.State | Should -Be 2
        }

        It 'a missing slices document is NotEvaluated, and the tracker is never read' {
            Mock Get-TrackerIssue { New-Tracker }

            $r = Invoke-DriftCheck -SlicesPath (Join-Path $TestDrive 'nothing-here.md')

            $r.State | Should -Be 'NotEvaluated'
            $r.Failures.Reason | Should -Contain 'SlicesDocMissing'
            Should -Invoke Get-TrackerIssue -Exactly -Times 0
        }

        It 'a criterion numbered for another slice is unparseable, not silently filed' {
            $path = New-SlicesDoc -Content @'
# Slices

## S1 — A slice

Acceptance:
  - S1.1 The first criterion.
  - S2.4 Numbered for a slice this is not.
'@ -Name 'stray.md'
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' -Body '- [ ] **S1.1** first'
            ) }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.State | Should -Be 'NotEvaluated'
            $r.Failures.Reason | Should -Contain 'UnparseableCriterion'
        }

        It 'drift AND a failed comparison resolves to exit 2, never 1' {
            # 2 takes precedence: a run that found drift and also failed to finish is an
            # incomplete run, and reporting it as a finished one is the failure I12 forbids.
            $path = New-SlicesDoc -Content $script:TwoCriterionDoc -Name 'both.md'
            Mock Get-TrackerIssue { New-Tracker -Issues @(
                New-Issue -Number 9 -Title 'S1 — A slice' `
                    -Body "- [ ] **S1.1** first`n``design/30-slices.md`` § S1 @ ``deadbee``"
            ) }
            Mock Test-CommitIsAncestor { 'Unresolvable' }

            $r = Invoke-DriftCheck -SlicesPath $path

            $r.Findings.Kind | Should -Contain 'InDocNotIssue'
            $r.Failures.Reason | Should -Contain 'PinUnresolvable'
            $r.State | Should -Be 'NotEvaluated'
            Get-DriftExitCode -State $r.State | Should -Be 2
        }
    }

    Context 'exit code map' {

        It 'maps each state, and refuses an unknown one rather than defaulting to 0' {
            Get-DriftExitCode -State 'Clean'        | Should -Be 0
            Get-DriftExitCode -State 'Drifted'      | Should -Be 1
            Get-DriftExitCode -State 'NotEvaluated' | Should -Be 2
            { Get-DriftExitCode -State 'Something' } | Should -Throw
        }
    }

    Context 'default -SlicesPath resolves against the calling repo, not this script''s own location' {
        # Run as a real child process so the invocation-guard block (which every dot-sourced
        # test above never reaches - it exits) actually executes, with its working directory set
        # to an empty $TestDrive folder rather than this kit repo. Before the fix, the default
        # was `Join-Path (Split-Path -Parent $PSScriptRoot) 'design/30-slices.md'`, which
        # resolves to THIS kit repo's own real design/30-slices.md regardless of caller cwd - a
        # run from a repo with no slices document would silently drift-check the kit's own
        # document (and shell out to gh for the kit's own tracker) instead of reporting
        # SlicesDocMissing for the caller's (nonexistent) one. A missing-file default is used
        # rather than a comparison outcome because Get-SliceCriteria fails before any gh call,
        # so this stays network-free regardless of what gh is authenticated as on the runner.

        It 'exits 2 (NotEvaluated/SlicesDocMissing) when the calling repo has no design/30-slices.md, even though the kit repo does' {
            $callerRepo = Join-Path $TestDrive 'caller-repo'
            New-Item -ItemType Directory -Path $callerRepo -Force | Out-Null

            Push-Location $callerRepo
            try {
                $output = & pwsh -NoProfile -File $script:ScriptPath
                $exitCode = $LASTEXITCODE
            } finally {
                Pop-Location
            }

            $exitCode | Should -Be 2
            ($output -join "`n") | Should -Match 'SlicesDocMissing'
        }
    }
}
