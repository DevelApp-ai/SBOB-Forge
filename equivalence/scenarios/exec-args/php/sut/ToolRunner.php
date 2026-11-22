<?php

namespace Equiv;

class ToolRunner
{
    public function runGitStatus(): array
    {
        $output = [];
        exec("git status", $output);
        return $output;
    }
}
