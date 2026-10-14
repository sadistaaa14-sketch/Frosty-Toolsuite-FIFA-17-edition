# Final response rule

How a turn in this project is closed out. It is the companion to `verify-before-claim`: that rule
governs *when* a claim may be made, this one governs *what the closing message must contain*.

Applies to every turn that changes code, files, configuration or builds. A pure answer to a
question is exempt from the evidence block, but still states its source.

## Non-negotiables

1. **Never claim anything is done, fixed, working, passing or complete without fresh evidence in the
   same response.** "Fresh" means produced after the last edit to the files involved. Show the
   command and the result, not a summary of what it would say.
2. **Report the real result, including the parts that failed.** A check that did not run, was
   skipped, or could not run is listed as a limitation — never folded into a pass and never left
   out because the rest was green.
3. **No success language without its number.** Name the count, the exit status, or the artifact:
   `passed: 182  failed: 0`, `Exit code: 0`, `0 errors`, `9.0:1 contrast`, `16:34:39 file`. A claim
   with no number attached is not a result.
4. **Fix causes, not checks.** Never weaken an assertion, delete a test, swallow an exception or add
   a suppression to reach green. If the requested behaviour genuinely requires changing a check, say
   so in the response and verify the behaviour instead.
5. **Correct earlier claims out loud.** When new evidence contradicts something said in an earlier
   turn or earlier in the same turn, state the correction plainly. A quietly abandoned claim is
   worse than the wrong claim.
6. **Preserve the tested command's exit status** when filtering output through a pipeline
   (`set -o pipefail`, or capture `$?` explicitly). A filtered pipeline that succeeds while the
   command failed is a false pass.

## Shape of the closing message

Lead with the outcome in the first sentence. Someone should not have to read to the end to learn
whether the thing works. Then:

```
**What was verified**    one line per claim, each with its command/result
**Checks that failed or could not run**   named explicitly, with the reason
**Limitations**          what the evidence does not cover, and what the user must do to cover it
**Files**                clickable links to everything created or changed
**Corrections**          only when something previously stated was wrong
```

Keep prose short and non-repetitive; the user is reading a live transcript. No filler, no
restating the request back, no "I hope this helps".

## Evidence this project accepts

- `.freebuff/verify/run.sh` — builds the projects, compiles the harness against the built
  assemblies, runs it. Quote `passed:` / `failed:` and `EXIT=`.
- An MSBuild run for anything touching project structure, quoting the error count.
- A `git status` / mtime comparison when it matters that the verified artifact post-dates the edit —
  otherwise the green result may belong to superseded sources.
- For behaviour that needs a loaded game, the code path read end to end, reported as
  read-not-run, plus exactly what the user should do to confirm it in the editor.

## Time-boxed work

When a task is time-boxed (a "30 minute" phase), record wall-clock start and end in
`.freebuff/worklog/`, keep worked time separate from waiting time, and state the true elapsed time
in the closing message. If the phase ended short, say so and say what is left — do not present a
partial phase as a finished one. Log corrections to the phase as their own entry.

## Findings during review

A review or Q&A phase reports what it finds, with file:line evidence, and separates:
`fixed and verified`, `reported, not fixed (with the reason)`, and `pre-existing, not mine`.
Scope creep is not allowed silently; a fix to another project needs its own verification before it
may be described as done.

## Ending the turn

Finish the prose first, then offer two to four follow-up prompts: at least one close next step and
at least one that opens a genuinely new direction. Prompts name an outcome, not a plan.
