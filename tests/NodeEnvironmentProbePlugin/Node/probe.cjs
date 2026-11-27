'use strict';

const fs = require('node:fs');
const { createRequire } = require('node:module');
const { setTimeout: delay } = require('node:timers/promises');

async function main() {
    const [environmentPackageJson, requestFile, resultFile] = process.argv.slice(2);
    const fromEnvironment = createRequire(environmentPackageJson);
    const isNumber = fromEnvironment('is-number');
    const packageVersion = fromEnvironment('is-number/package.json').version;
    const request = JSON.parse(fs.readFileSync(requestFile, 'utf8'));
    console.log(`PID ${process.pid} | Node ${process.version} | is-number ${packageVersion}`);
    console.log(`运行目录：${process.cwd()}`);
    if (request.scenario === 'fail') {
        console.error('预期进程失败：这是用户选择的测试场景。');
        process.exitCode = 42;
        return;
    }
    const rounds = request.scenario === 'long' ? 60 : 1;
    let rows;
    for (let step = 1; step <= rounds; step++) {
        rows = request.values.map(input => {
            const valid = isNumber(input) && Number.isFinite(Number(input));
            return { input, valid, value: valid ? Number(input) : null };
        });
        console.log(`PROGRESS ${step} ${rounds}`);
        if (request.scenario === 'long') await delay(1000);
    }
    const numbers = rows.filter(row => row.valid).map(row => row.value);
    const sum = numbers.length ? numbers.reduce((total, value) => total + value, 0) : null;
    const mean = sum === null ? null : sum / numbers.length;
    const overflow = sum !== null && (!Number.isFinite(sum) || !Number.isFinite(mean));
    const report = {
        schema: 1,
        nodeVersion: process.version,
        packageVersion,
        processId: process.pid,
        executablePath: process.execPath,
        environmentPackageJson,
        scenario: request.scenario,
        rows,
        validCount: numbers.length,
        invalidCount: rows.length - numbers.length,
        sum: overflow ? null : sum,
        mean: overflow ? null : mean,
        aggregateError: overflow ? '累加过程中超出浮点范围，未将 Infinity 当作统计结果。' : null
    };
    fs.writeFileSync(resultFile, JSON.stringify(report, null, 2), { encoding: 'utf8', flag: 'wx' });
    console.log(`处理完成：${rows.length} 项，结果已保存。`);
    console.error('中文诊断：Node 进程正常结束。');
}

main().catch(error => {
    console.error(error.stack || String(error));
    process.exitCode = 1;
});
