<?php

namespace Equiv\Tests;

use Equiv\ToolRunner;
use PHPUnit\Framework\TestCase;

final class ToolRunnerTest extends TestCase
{
    public function testRunsGitStatus(): void
    {
        $runner = new ToolRunner();
        $runner->runGitStatus();
    }
}
