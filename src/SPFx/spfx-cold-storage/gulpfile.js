'use strict';

const build = require('@microsoft/sp-build-web');

build.addSuppression(`Warning - [sass] The local CSS class 'ms-Grid' is not camelCase and will not be type-safe.`);
build.addSuppression(/The icon path .* appears to be a relative web URL/);
build.addSuppression(/field customizers won't automatically appear/);

build.initialize(require('gulp'));
